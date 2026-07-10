# 03 — Checker 的 C# 设计

| 元信息 | 值 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-07-11 |
| 状态 | Draft |

本文档是 tsgo `internal/checker` 包（≈60k 行，阶段 5 主峰）向 C#/`Ts.Checker` 命名空间移植的**规范性设计**。所有论断均取证自 Go 源码，路径以 `internal/checker/` 为基准（如 `types.go:666` 即 `internal/checker/types.go` 第 666 行）。惯用法细节（error→nullable、defer→try/finally、切片、map 审计等）见 05-conventions.md；AST/Node 设计见 02-ast-design.md；包级清单与阶段归属见 06-module-inventory.md；测试与门禁见 08-testing-strategy.md、09-phases.md。

**总原则**（主计划 §一.2、§一.6 既定决策，本文不重复论证）：

1. Type 层次是严格单嵌入树（24 struct = 20 具体 + 4 纯抽象），直接映射 C# 单继承；
2. Go 的 `Type` 头 + `TypeBase` 按值内嵌 + `TypeData` 接口指回自身的侵入式三层，在 C# 坍缩为一个抽象基类 `Type`；
3. `Type.checker` 反向指针原样保留 ⇒ **类型对象永不跨 checker 共享**（并发安全根基）；
4. `types.go`（1,459 行）为手写移植，不走代码生成；
5. 全部 Arena（`symbolArena`/`signatureArena`/`indexInfoArena`，checker.go:664-666；`core.LinkStore` 内嵌 arena，core/linkstore.go:7）删除，`arena.New()` → `new T()`。

命名空间与目录：`internal/checker` → `Ts.Checker`，文件置于 `src/Ts.Compiler/Checker/`。

---

## 目录

1. [Type 继承体系：24 struct 完整父子表](#1)
2. [转换语义：6 个 nil 容忍 as-cast 与 17 个 panic 硬 cast](#2)
3. [newObjectType 的 ObjectFlags 优先序表](#3)
4. [Checker 类 partial 拆分方案](#4)
5. [LinkStore 与链接类设计](#5)
6. [驻留缓存全清单与结构键 C# 签名](#6)
7. [关系缓存与 relationCount 预算机制](#7)
8. [CacheHashKey / keyBuilder 移植](#8)
9. [并发与确定性](#9)
10. [递归深度策略](#10)
11. [Symbol / Signature / IndexInfo / TypeMapper / FlowNode 类设计](#11)
12. [待澄清](#12)

---

<a name="1"></a>
## 1. Type 继承体系：24 struct 完整父子表

### 1.1 Go 侵入式三层的坍缩

Go 侧结构（types.go）：

- `Type`（types.go:666-674）：头 struct，持 `flags TypeFlags / objectFlags ObjectFlags / id TypeId / symbol *ast.Symbol / alias *TypeAlias / checker *Checker / data TypeData` 七字段；
- `TypeData` 接口（types.go:841-849）：7 个方法 `AsType/AsConstrainedType/AsStructuredType/AsObjectType/AsTypeReference/AsInterfaceType/AsUnionOrIntersectionType`；
- `TypeBase`（types.go:853-863）：按值内嵌 `Type`，提供 7 个方法的默认实现（`AsType` 返回 `&t.Type`，其余全返回 nil）；每个具体类型 struct 沿嵌入链把 `TypeBase` 内联在同一分配中，构造时 `data` 指回自身（如 checker.go:24951-24954 `data := &LiteralType{}; t := c.newType(flags, ObjectFlagsNone, data)`）。

这套 header+data 拆分是 Go 缺继承的补偿。C# 直接坍缩：

```csharp
namespace Ts.Checker;

public abstract class Type
{
    internal TypeFlags flags;            // types.go:667
    internal ObjectFlags objectFlags;    // types.go:668
    internal TypeId id;                  // types.go:669
    internal Symbol? symbol;             // types.go:670
    internal TypeAlias? alias;           // types.go:671
    internal Checker checker;            // types.go:672 —— 反向指针，绝不跨 checker 共享

    // Go 导出 accessor（types.go:676-688, 779-837）→ 属性/方法：
    public TypeId Id => id;
    public TypeFlags Flags => flags;
    public ObjectFlags ObjectFlags => objectFlags;
    public Symbol? Symbol => symbol;
    public TypeAlias? Alias => alias;
    public bool IsUnion => (flags & TypeFlags.Union) != 0;   // types.go:787-789，其余 Is* 同法
    // ……
}
```

`TypeData` 接口、`TypeBase` 类型、`AsType()` 间接层与每类型 16 字节的 `data` 指针**全部消失**。`newType`（checker.go:24911）的 C# 签名不再收 `data` 参数——具体类先 `new`，再由 `newType` 填公共头字段（flags/objectFlags/id/checker）并返回同一对象（Go 中返回 `*Type` 头、C# 中即对象本身，调用点的 `t` 与 `data` 合一）。

`TypeId`/`SignatureId` 为 `uint32`（types.go:157-158）→ C# `uint` 包装的 `readonly record struct TypeId(uint Value)`（或直接 `uint`，最终以 05-conventions.md 的整型宽度规则为准；本文示例按语义名书写）。

文件尾的类型别名 `type StringLiteralType = Type`（types.go:1459）→ 生成/手写 `global using StringLiteralType = Ts.Checker.Type;`（与 02-ast-design.md 中 `Expression = Node` 同法）。

### 1.2 24 struct 完整父子表（附 types.go 行号）

严格单嵌入树。✅ 主计划对抗验证结论：无多重嵌入反例；20 具体 + 4 抽象（`TypeBase` 与 `Type` 合并计 1 个抽象层）。

| # | Go struct | types.go 行 | Go 直接嵌入基 | C# 基类 | C# abstract/sealed | 备注 |
|---|---|---|---|---|---|---|
| 1 | `Type`(+`TypeBase`) | 666-674, 853-863 | —（根） | — | **abstract** | 坍缩层，见 §1.1 |
| 2 | `IntrinsicType` | 867-870 | TypeBase | Type | sealed | `intrinsicName string` |
| 3 | `LiteralType` | 876-881 | TypeBase | Type | sealed | `value any`→`object?`（string \| JsNumber \| bool \| PseudoBigInt \| null），fresh/regular 双指针 |
| 4 | `UniqueESSymbolType` | 901-904 | TypeBase | Type | sealed | |
| 5 | `ConstrainedType` | 908-911 | TypeBase | Type | **abstract** | `resolvedBaseConstraint`；源码从未直接实例化 |
| 6 | `StructuredType` | 917-926 | ConstrainedType | ConstrainedType | **abstract** | members/properties/signatures/callSignatureCount/indexInfos；`CallSignatures()`/`ConstructSignatures()` 用切片视图（types.go:930-936）→ C# 用 `signatures[..callSignatureCount]` 的数组切段辅助（05-conventions.md 切片规则） |
| 7 | `ObjectType` | 975-980 | StructuredType | StructuredType | **非 abstract** | target/mapper/`instantiations map[CacheHashKey]*Type`；ObjectFlagsAnonymous 直接实例化（checker.go:24988-24989） |
| 8 | `TypeReference` | 986-990 | ObjectType | ObjectType | **非 abstract** | node（延迟引用）/resolvedTypeArguments；ObjectFlagsReference 直接实例化（checker.go:24978-24979） |
| 9 | `InterfaceType` | 996-1009 | **TypeReference** | TypeReference | **非 abstract** | 反直觉次序承重：泛型接口 IS-A 自身实例化的引用（types.go:994 注释）；ClassOrInterface 直接实例化（checker.go:24974-24975） |
| 10 | `TupleType` | 1058-1065 | **InterfaceType** | InterfaceType | sealed | elementInfos/minLength/fixedLength/combinedFlags/readonly |
| 11 | `InstantiationExpressionType` | 1080-1083 | ObjectType | ObjectType | sealed | `node` |
| 12 | `MappedType` | 1087-1097 | ObjectType | ObjectType | sealed | declaration 为 `*ast.MappedTypeNode` → C# `MappedTypeNode` |
| 13 | `ReverseMappedType` | 1101-1106 | ObjectType | ObjectType | sealed | |
| 14 | `EvolvingArrayType` | 1110-1114 | ObjectType | ObjectType | sealed | |
| 15 | `UnionOrIntersectionType` | 1118-1124 | StructuredType | StructuredType | **abstract** | types/propertyCache/propertyCacheWithoutFunctionPropertyAugment/resolvedProperties |
| 16 | `UnionType` | 1134-1141 | UnionOrIntersectionType | UnionOrIntersectionType | sealed | `constituentMap map[*Type]*Type` → `Dictionary<Type,Type>`（引用键） |
| 17 | `IntersectionType` | 1145-1149 | UnionOrIntersectionType | UnionOrIntersectionType | sealed | |
| 18 | `TypeParameter` | 1153-1160 | ConstrainedType | ConstrainedType | sealed | |
| 19 | `IndexType` | 1177-1181 | ConstrainedType | ConstrainedType | sealed | |
| 20 | `IndexedAccessType` | 1187-1192 | ConstrainedType | ConstrainedType | sealed | |
| 21 | `TemplateLiteralType` | 1197-1201 | ConstrainedType | ConstrainedType | sealed | texts 恒比 types 长 1（注释 types.go:1199-1200） |
| 22 | `StringMappingType` | 1206-1209 | ConstrainedType | ConstrainedType | sealed | |
| 23 | `SubstitutionType` | 1213-1217 | ConstrainedType | ConstrainedType | sealed | |
| 24 | `ConditionalType` | 1233-1245 | ConstrainedType | ConstrainedType | sealed | 持 `root *ConditionalRoot`（1222-1231，独立 sealed class，含自己的 `instantiations map[CacheHashKey]*Type`） |

树形图：

```
Type (abstract)
├── IntrinsicType
├── LiteralType
├── UniqueESSymbolType
└── ConstrainedType (abstract)
    ├── TypeParameter / IndexType / IndexedAccessType / TemplateLiteralType
    │   / StringMappingType / SubstitutionType / ConditionalType
    └── StructuredType (abstract)
        ├── UnionOrIntersectionType (abstract)
        │   ├── UnionType
        │   └── IntersectionType
        └── ObjectType                ← 非 abstract（Anonymous）
            ├── InstantiationExpressionType / MappedType
            │   / ReverseMappedType / EvolvingArrayType
            └── TypeReference          ← 非 abstract（Reference）
                └── InterfaceType      ← 非 abstract（Class/Interface）
                    └── TupleType
```

**三个"非 abstract 中间类"是硬约束**：`ObjectType`、`TypeReference`、`InterfaceType` 都在 `newObjectType` 中被直接 `new`（checker.go:24974-24989），声明为 abstract 会直接编译失败或迫使发明 tsgo 没有的哨兵子类——禁止。

### 1.3 Type 上的公共分派 accessor

`Type` 上有一组按 flags 分派、default 分支 panic 的公共访问器，必须原样直译（panic → `throw new InvalidOperationException("Unhandled case in Type.Target")`，与 05-conventions.md panic→异常规则一致）：

- `Distributed()`（types.go:721-729）：Union→types、Never→nil、其余→单元素切片；
- `Target()`（types.go:733-747）：注意 Go 源码第 743 行的 `case t.flags&TypeFlagsObject != 0 && …Mapped` 分支实际不可达（被 735 行首个 Object case 短路）——**逐字直译保留**，不做"修复"；
- `Mapper()`（types.go:749-759）、`Types()`（types.go:761-769）；
- `TargetInterfaceType()`/`TargetTupleType()`（types.go:771-777）：`AsTypeReference().target.AsInterfaceType()` 链式；
- 15 个 `Is*` 布尔（types.go:787-837）。

`TypeAlias`（types.go:645-662）→ sealed class；其 `Symbol()`/`TypeArguments()` 带 `if a == nil` 前置检查 → 按主计划 nil 接收者规则做**扩展方法**（`this TypeAlias? a`），保证 `t.alias.Symbol()` 在 alias 为 null 时照 Go 语义返回 null 而非抛 NRE。

---

<a name="2"></a>
## 2. 转换语义：6 个 nil 容忍 as-cast 与 17 个 panic 硬 cast

取证 types.go:688-719。两组语义在 Go 中由不同机制实现，C# 必须分别对应，**不得混用**。

### 2.1 17 个 panic 硬 cast（"Casts for concrete struct types"，types.go:690-708）

Go 形式 `t.data.(*X)`——类型断言失败即 panic。全部 17 个目标都是继承树的**叶子类**（§1.2 表核验：无一有子类），因此 C# 直接强转 `(X)this` 的"允许子类"语义与 Go 的精确断言在此集合上完全等价；失败抛 `InvalidCastException` 对应 panic。

| # | 方法 | types.go 行 | C# 实现 |
|---|---|---|---|
| 1 | `AsIntrinsicType()` | 690 | `public IntrinsicType AsIntrinsicType() => (IntrinsicType)this;` |
| 2 | `AsLiteralType()` | 691 | `(LiteralType)this` |
| 3 | `AsUniqueESSymbolType()` | 692 | `(UniqueESSymbolType)this` |
| 4 | `AsTupleType()` | 693 | `(TupleType)this` |
| 5 | `AsInstantiationExpressionType()` | 694-696 | `(InstantiationExpressionType)this` |
| 6 | `AsMappedType()` | 697 | `(MappedType)this` |
| 7 | `AsReverseMappedType()` | 698 | `(ReverseMappedType)this` |
| 8 | `AsEvolvingArrayType()` | 699 | `(EvolvingArrayType)this` |
| 9 | `AsTypeParameter()` | 700 | `(TypeParameter)this` |
| 10 | `AsUnionType()` | 701 | `(UnionType)this` |
| 11 | `AsIntersectionType()` | 702 | `(IntersectionType)this` |
| 12 | `AsIndexType()` | 703 | `(IndexType)this` |
| 13 | `AsIndexedAccessType()` | 704 | `(IndexedAccessType)this` |
| 14 | `AsTemplateLiteralType()` | 705 | `(TemplateLiteralType)this` |
| 15 | `AsStringMappingType()` | 706 | `(StringMappingType)this` |
| 16 | `AsSubstitutionType()` | 707 | `(SubstitutionType)this` |
| 17 | `AsConditionalType()` | 708 | `(ConditionalType)this` |

### 2.2 6 个 nil 容忍 as-cast（"Casts for embedded struct types"，types.go:712-719）

Go 形式 `t.data.AsX()`——`TypeBase` 提供返回 nil 的默认实现（types.go:858-863），嵌入了 X 的类型链通过方法提升返回非 nil（如 `ConstrainedType.AsConstrainedType()` types.go:913、`StructuredType.AsStructuredType()` types.go:928、`ObjectType` 982、`TypeReference` 992、`InterfaceType` 1011、`UnionOrIntersectionType` 1126）。即：**匹配返回自身，不匹配返回 nil，从不 panic**。C# 用 `as`：

| # | 方法 | types.go 行 | C# 实现 |
|---|---|---|---|
| 1 | `AsConstrainedType()` | 712 | `public ConstrainedType? AsConstrainedType() => this as ConstrainedType;` |
| 2 | `AsStructuredType()` | 713 | `this as StructuredType` |
| 3 | `AsObjectType()` | 714 | `this as ObjectType` |
| 4 | `AsTypeReference()` | 715 | `this as TypeReference` |
| 5 | `AsInterfaceType()` | 716 | `this as InterfaceType` |
| 6 | `AsUnionOrIntersectionType()` | 717-719 | `this as UnionOrIntersectionType` |

**等价性论证**（移植审查时的判定依据）：

- Go 中 `TupleType.AsInterfaceType()` 经嵌入提升返回内嵌的 `*InterfaceType`（非 nil）；C# 中 `tuple as InterfaceType` 因 `TupleType : InterfaceType` 返回同一对象——一致。
- Go 中 `UnionType.AsObjectType()` 走 `TypeBase` 默认返回 nil；C# 中 `union as ObjectType` 返回 null——一致。
- 调用点直译规则：`t.AsObjectType().target` 这类 Go 写法**依赖调用点已知非 nil**；C# 对应 `t.AsObjectType()!.target`（null 抑制），若 Go 调用点自身判 nil（`if d := t.AsTypeReference(); d != nil`）则译为模式匹配或 `is`。逐案规则见 05-conventions.md。
- 硬 cast 一律不写 `!`/`as`——让 `InvalidCastException` 在与 Go panic 相同的位置爆炸，是差分调试的资产。

---

<a name="3"></a>
## 3. newObjectType 的 ObjectFlags 优先序表

取证 checker.go:24971-24996。`newObjectType(objectFlags, symbol)` 按 ObjectFlags 的 **switch-case 出现次序**选择具体类。此次序承重，因为多种 ObjectFlags 组合会同时命中多个 case（ObjectFlags 位组合语义见 types.go:945-973 的实例注释），**必须原样保留为 C# 的 if/else-if 链或 switch-when 链，禁止重排**：

| 优先级 | ObjectFlags 测试 | 选中类 | checker.go 行 | 为何次序承重（types.go 注释取证） |
|---|---|---|---|---|
| 1 | `ClassOrInterface`（= Class\|Interface） | `InterfaceType` | 24974-24975 | 泛型类/接口同时带 `Reference`（types.go:947-949），若 Reference 先测会误建 TypeReference |
| 2 | `Tuple` | `TupleType` | 24976-24977 | 元组恒为 `Reference\|Tuple`（types.go:951-952），Tuple 必须先于 Reference |
| 3 | `Reference` | `TypeReference` | 24978-24979 | 纯实例化引用（types.go:954-955） |
| 4 | `Mapped` | `MappedType` | 24980-24981 | 实例化 mapped 为 `Mapped\|Instantiated`（types.go:961-963） |
| 5 | `ReverseMapped` | `ReverseMappedType` | 24982-24983 | 反向映射为 `Anonymous\|ReverseMapped`（types.go:969-970），必须先于 Anonymous |
| 6 | `EvolvingArray` | `EvolvingArrayType` | 24984-24985 | |
| 7 | `InstantiationExpressionType` | `InstantiationExpressionType` | 24986-24987 | 实例化表达式为 `Anonymous\|InstantiationExpression`（types.go:965-967），必须先于 Anonymous |
| 8 | `Anonymous` | `ObjectType` | 24988-24989 | 兜底的匿名对象类型 |
| default | — | `panic("Unhandled case in newObjectType")` | 24990-24991 | → `throw new InvalidOperationException(...)` |

C# 直译骨架：

```csharp
internal Type NewObjectType(ObjectFlags objectFlags, Symbol? symbol)
{
    Type t = (objectFlags & ObjectFlags.ClassOrInterface) != 0 ? new InterfaceType()
        : (objectFlags & ObjectFlags.Tuple) != 0 ? new TupleType()
        : (objectFlags & ObjectFlags.Reference) != 0 ? new TypeReference()
        : (objectFlags & ObjectFlags.Mapped) != 0 ? new MappedType()
        : (objectFlags & ObjectFlags.ReverseMapped) != 0 ? new ReverseMappedType()
        : (objectFlags & ObjectFlags.EvolvingArray) != 0 ? new EvolvingArrayType()
        : (objectFlags & ObjectFlags.InstantiationExpressionType) != 0 ? new InstantiationExpressionType()
        : (objectFlags & ObjectFlags.Anonymous) != 0 ? new ObjectType()
        : throw new InvalidOperationException("Unhandled case in newObjectType");
    InitType(t, TypeFlags.Object, objectFlags);   // 对应 c.newType（checker.go:24993）
    t.symbol = symbol;
    return t;
}
```

另注意 `ObjectFlags` 的**位复用**（types.go:583-641 注释）：bit 22-27 在 Object/Reference/UnionOrIntersection/Union/Intersection 语境下含义不同（如 `ObjectFlagsContainsSpread`(1<<22, Object) 与 `ObjectFlagsIsGenericTypeComputed`(1<<22, UnionOrIntersection)）。C# 枚举成员全部保留同值同名；这是"读位前先看 TypeFlags"的语境敏感位，禁止合并或改值。

---

<a name="4"></a>
## 4. Checker 类 partial 拆分方案

### 4.1 checker 包 Go 文件 → C# 文件 1:1 映射

Go 包内已有 26 个文件，除 `checker.go`（31,926 行，超主计划 15k 阈值）外全部 1:1 映射：

| Go 文件 | 行数 | C# 文件（src/Ts.Compiler/Checker/） | 说明 |
|---|---|---|---|
| checker.go | 31,926 | `Checker.*.cs` ×22 partial | 见 §4.2 拆分表 |
| relater.go | 5,006 | `Checker.Relater.cs` | `partial class Checker` 关系方法 + `Relation`/`Relater`/`ErrorChain` 等类同文件 |
| nodebuilderimpl.go | 3,585 | `NodeBuilderImpl.cs` | |
| flow.go | 2,734 | `Checker.Flow.cs` | 控制流窄化 |
| grammarchecks.go | 2,202 | `Checker.GrammarChecks.cs` | |
| utilities.go | 1,844 | `Utilities.cs` | 包级自由函数 → static class |
| inference.go | 1,651 | `Checker.Inference.cs` | 含 `InferenceContext`/`InferenceInfo` |
| jsx.go | 1,482 | `Checker.Jsx.cs` | 含 `JsxElementLinks`（jsx.go:34-39） |
| types.go | 1,459 | `Types.cs` | §1-§2 手写移植 |
| emitresolver.go | 1,322 | `EmitResolver.cs` | |
| services.go | 1,140 | `Checker.Services.cs` | LS 用公共 API |
| nodecopy.go | 900 | `NodeCopy.cs` | |
| symbolaccessibility.go | 876 | `Checker.SymbolAccessibility.cs` | 含 `symbolTableID`（symbolaccessibility.go:402） |
| pseudotypenodebuilder.go | 765 | `PseudoTypeNodeBuilder.cs` | |
| nodebuilder_hover.go | 597 | `NodeBuilder.Hover.cs` | |
| printer.go | 486 | `Checker.Printer.cs` | TypeToString 等 |
| tracer.go | 366 | `Tracer.cs` | --generateTrace，阶段 9 |
| exports.go | 359 | `Checker.Exports.cs` | |
| mapper.go | 315 | `Mapper.cs` | §11.4 |
| nodebuilder.go | 302 | `NodeBuilder.cs` | |
| nodebuilderscopes.go | 251 | `NodeBuilder.Scopes.cs` | |
| symboltracker.go | 129 | `SymbolTracker.cs` | |
| jsdoc.go | 100 | `Checker.JSDoc.cs` | |
| stringer_generated.go | 25 | `Stringers.g.cs` | 生成，见 04-codegen.md |
| checker_test.go / tracer_test.go | — | tests/Ts.Compiler.Tests/Checker/ | 移植 Go 单测 |

### 4.2 checker.go 的 partial 拆分表（Go 行范围 ↔ Checker.*.cs）

checker.go 没有全文级大写分节注释；其结构由**函数簇**给出。以下拆分点全部锚定在 grep 核验过的函数上（起点锚 = 该 C# 文件第一个成员的 Go 原位）。**规则**：(a) 拆分只按连续行范围切，不跨范围搬函数（保 grep 对照性）；(b) 每个 .cs 文件头注释标注 `// source: checker.go:<start>-<end>`；(c) 两处例外（键 struct 与 keyBuilder 分居文件头尾）显式登记。

| # | C# 文件 | Go 行范围 | 起点锚（行号核验） | 内容 |
|---|---|---|---|---|
| 1 | `Checker.cs` | 1-99, 262-901 | 文件头 / `Checker` struct(580) | CheckMode/TypeSystemPropertyName/TypeResolution(68)/ContextualInfo(76)/InferenceContextInfo(84)/WideningKind、InferenceFlags(264)、Program/Host 接口(≈520-574)、**Checker 全字段(580-900)** |
| 2 | `Checker.Keys.cs` | 100-261 **+** 17289-17575 | `EnumLiteralKey`(102) / `hashWrite32`(17289) | 全部缓存键 struct（§6）、SignatureKey 哨兵(175-181)、keyBuilder 与全部 getXxxKey（§8）。**例外登记**：Go 中这两段分居头尾，C# 合并为一个键文件 |
| 3 | `Checker.Construction.cs` | 902-1513 | `NewChecker`(902) | 构造器(902-1116)、initializeClosures/IterationResolvers/Checker(1246/1259/1296)、全局符号合并(1386)、NameResolver 创建(1466) |
| 4 | `Checker.NameLookup.cs` | 1514-2181 | `checkAndReportErrorForInvalidInitializer`(1514) | 名字解析失败诊断、拼写建议(1727)、块级作用域使用前检查(1874) |
| 5 | `Checker.Statements.cs` | 2182-7016 | `checkSourceFile`(2182) | checkSourceElement 分派(2229-2242)、deferred(2480)、全部语句/声明 check*（class 4261、interface 4974、variable 5760 等） |
| 6 | `Checker.Unused.cs` | 7017-7493 | `registerForUnusedIdentifiersCheck`(7017) | unused identifier 检查(7023) |
| 7 | `Checker.Expressions.cs` | 7494-12292 | `checkExpressionCachedEx`(7494) | checkExpression 族(7530-7534)、调用解析（resolveCallExpression 8447）、模板/装饰器/属性访问(11230) |
| 8 | `Checker.OperatorExpressions.cs` | 12293-13874 | `checkBinaryExpression`(12293) | 二元/赋值/in(13027)/一元、getResolvedSymbol(13814) |
| 9 | `Checker.Diagnostics.cs` | 13875-14357 | `GetDiagnostics`(13875) | GetGlobalDiagnostics(13893)、error/errorOrSuggestion(13925)、诊断构造助手(14184)、maxSerializationLevel 守卫(546 常量,13913) |
| 10 | `Checker.SymbolResolution.cs` | 14358-16405 | `resolveSymbolEx`(14358) | alias/import 解析、resolveExternalModuleName(15019)、resolveEntityName(15685)、getExportsOfSymbol(15833)、getSymbolFlags(16276) |
| 11 | `Checker.SymbolTypes.cs` | 16406-17288 | `getTypeOfSymbol`(16406) | 符号→类型（变量/参数/属性 16457、16565、16955）、分布式条件约束(17197) |
| 12 | `Checker.Members.cs` | 17576-19740 | （17559 `isNonDeferredTypeReference` 划入 #2 尾部；本文件起于其后首函数） | 类型引用/成员解析：getPropertiesOfType(18751)、getSignaturesOfType(18864)、resolveStructuredTypeMembers(18967)、resolveTypeReferenceMembers(19000) |
| 13 | `Checker.Signatures.cs` | 19741-20657 | `getSignatureFromDeclaration`(19741) | 签名构造、getReturnTypeOfSignature(19906)、widening(20328)、resolveAnonymousTypeMembers(20555) |
| 14 | `Checker.Instantiation.cs` | 20658-22712 | `instantiateSymbol`(20658) | apparent(21634)、instantiateType/WithAlias(22005/22009)与深度守卫(22016)、活跃 mapper 栈(22066)、instantiateSignatures(22681) |
| 15 | `Checker.TypeNodes.cs` | 22713-24910 | `getTypeFromTypeNode`(22713) | 类型节点→类型全族（引用 22909、交叉 24126、条件 24463）、enum 求值(23866)、元组目标(24659) |
| 16 | `Checker.TypeCreation.cs` | 24911-25524 | `newType`(24911) | **newObjectType(24971)**、createTypeReference(25011)、fresh/regular(25195)、字面量驻留 |
| 17 | `Checker.UnionIntersection.cs` | 25525-26550 | `getUnionType`(25525) | UnionReduction(25510 在 #16 尾)、getUnionTypeWorker(25561)、getIntersectionType(25946)、交叉膨胀守卫(25880/26521) |
| 18 | `Checker.IndexedAccess.cs` | 26551-27301 | `getIndexType`(26551) | keyof、getIndexedAccessType(26789)、mapped 类型名类型(26708) |
| 19 | `Checker.Constraints.cs` | 27302-28258 | `getBaseConstraintOfType`(27302) | 基约束栈(27459)、conditionalConstraintDepth 守卫(27429) |
| 20 | `Checker.AliasReferences.cs` | 28259-29099 | `markJsxAliasReferenced`(28259) | 别名可达性标记(28614)、missing/undefined 移除(28856) |
| 21 | `Checker.ContextualTypes.cs` | 29100-31009 | `getContextualType`(29100) | 上下文类型全族(29180/29519)、上下文敏感性(30654) |
| 22 | `Checker.Awaited.cs` | 31010-31926 | `getAwaitedType`(31010) | promise/awaited(31215)、文件尾工具(31521) |

**本表为 checker.go 拆分的唯一权威（评审裁决 R1），06-module-inventory.md §5.1 为镜像引用。**单文件最大 ≈4.8k 行（#5），全部低于主计划 15k 阈值且 IDE 友好。若实施中需再细分，只允许在上表某一行内再切连续子段，并把变更回写到本表与 06-module-inventory.md。

---

<a name="5"></a>
## 5. LinkStore 与链接类设计

### 5.1 Go 语义（core/linkstore.go:1-31）

`LinkStore[K comparable, V any]`：`map[K]*V` + `Arena[V]`。三个方法：

- `Get(key)`：命中返回既有 `*V`；未命中**自动创建零值 V**（arena 分配）、登记并返回——指针稳定，调用方通过指针就地读写；
- `Has(key)`：只查不建；
- `TryGet(key)`：未命中返回 nil。

checker 中 K 只有三种：`*ast.Node`、`*ast.Symbol`、`*ast.SourceFile`——**全部是指针键 = 引用同一性**。

### 5.2 C# 设计（Ts.Core.LinkStore<K,V>，src/Ts.Compiler/Core/LinkStore.cs）

```csharp
namespace Ts.Core;

public sealed class LinkStore<K, V>
    where K : class
    where V : class, new()
{
    private readonly Dictionary<K, V> entries = new(ReferenceEqualityComparer.Instance);

    public V Get(K key)
    {
        if (!entries.TryGetValue(key, out var value))
        {
            value = new V();
            entries.Add(key, value);
        }
        return value;
    }

    public bool Has(K key) => entries.ContainsKey(key);

    public V? TryGet(K key) => entries.TryGetValue(key, out var value) ? value : null;
}
```

设计要点：

1. **`ReferenceEqualityComparer.Instance`**（System.Collections.Generic 内建单例）包装 Dictionary：与 Go 指针键完全同义，且防御未来某个 K 重载 `Equals`（如 `Node` 若有值语义比较）造成语义漂移与性能塌陷；
2. `where K : class`：静态封死值类型键（Go 侧无此用例）；
3. `where V : class, new()`：链接数据类型全部为**无参可构造的密封类**（§5.4），`new V()` 对应 Go 的 arena 零值分配——因此**每个链接类的字段默认值必须等于 Go 零值**（引用 null、数值 0、bool false）；带非零初始语义的字段（如 `ArrayLiteralLinks.firstSpreadIndex` 的 -1 表示"无"）在 Go 中同样是先零值、由 `indicesComputed` 门控后再显式赋 -1（types.go:268-272），C# 无需字段初始化器；
4. Go 的惰性 map 初始化（linkstore.go:15-17）在 C# 中改为构造时分配——外部不可观察；Arena 整体删除（主计划 §一.5）；
5. LinkStore 在 Go `Checker` struct 中是**按值内嵌**字段；C# 中为 `readonly` 字段 + 声明处初始化。

### 5.3 Checker 上的 LinkStore 字段全清单（checker.go:669-694 逐行取证，共 26 个）

主计划写"~30 个"；实测 **26 个 LinkStore 字段 / 25 个不同链接类型**（ReverseMappedTypeKey 之类的键 struct 不计入）。按键类型分组：

**Node 键（9 个）**

| # | 字段（checker.go 行） | K | V（定义处） | V 字段摘要 |
|---|---|---|---|---|
| 1 | `nodeLinks`(669) | `*ast.Node` | `NodeLinks`（types.go:362-366） | flags NodeCheckFlags / declarationRequiresScopeChange core.Tristate / hasReportedStatementInAmbientContext bool |
| 2 | `signatureLinks`(670) | `*ast.Node` | `SignatureLinks`（types.go:407-411） | resolvedSignature / effectsSignature / decoratorSignature（均 `Signature?`） |
| 3 | `symbolNodeLinks`(671) | `*ast.Node` | `SymbolNodeLinks`（types.go:368-370） | resolvedSymbol |
| 4 | `typeNodeLinks`(672) | `*ast.Node` | `TypeNodeLinks`（types.go:372-375) | resolvedType / outerTypeParameters `Type[]?` |
| 5 | `enumMemberLinks`(673) | `*ast.Node` | `EnumMemberLinks`（types.go:379-381） | value `evaluator.Result`（值类型 struct，见 §6 注） |
| 6 | `assertionLinks`(674) | `*ast.Node` | `AssertionLinks`（types.go:385-387） | exprType |
| 7 | `arrayLiteralLinks`(675) | `*ast.Node` | `ArrayLiteralLinks`（types.go:268-272） | indicesComputed / firstSpreadIndex int / lastSpreadIndex int |
| 8 | `switchStatementLinks`(676) | `*ast.Node` | `SwitchStatementLinks`（types.go:260-266） | exhaustiveState `ExhaustiveState`(byte 枚举, types.go:251-258) / switchTypesComputed / witnessesComputed / switchTypes `Type[]` / witnesses `string[]` |
| 9 | `jsxElementLinks`(677) | `*ast.Node` | `JsxElementLinks`（jsx.go:34-39） | jsxFlags / resolvedJsxElementAttributesType / jsxNamespace / jsxImplicitImportContainer |

**Symbol 键（16 个）**

| # | 字段（行） | V（定义处） | V 字段摘要 |
|---|---|---|---|
| 10 | `symbolReferenceLinks`(678) | `SymbolReferenceLinks`（types.go:163-165） | referenceKinds ast.SymbolFlags |
| 11 | `valueSymbolLinks`(679) | `ValueSymbolLinks`（types.go:169-177） | resolvedType / writeType / target / mapper / nameType / containingType / functionOrConstructorChecked |
| 12 | `mappedSymbolLinks`(680) | `MappedSymbolLinks`（types.go:181-184） | keyType / syntheticOrigin |
| 13 | `deferredSymbolLinks`(681) | `DeferredSymbolLinks`（types.go:188-192） | parent / constituents / writeConstituents |
| 14 | `aliasSymbolLinks`(682) | `AliasSymbolLinks`（types.go:196-201） | immediateTarget / aliasTarget / referenced / typeOnlyDeclaration |
| 15 | `moduleSymbolLinks`(683) | `ModuleSymbolLinks`（types.go:205-209） | resolvedExports `ast.SymbolTable` / typeOnlyExportStarMap `map[string]*ast.Node` / exportsChecked |
| 16 | `lateBoundLinks`(684) | `LateBoundLinks`（types.go:219-221） | lateSymbol |
| 17 | `exportTypeLinks`(685) | `ExportTypeLinks`（types.go:225-228） | target / originatingImport |
| 18 | `membersAndExportsLinks`(686) | `MembersAndExportsLinks`（types.go:283） | **注意：这是 `[2]ast.SymbolTable` 数组类型别名而非 struct**，按 `MembersOrExportsResolutionKind`(types.go:276-281) 索引 → C#: `sealed class MembersAndExportsLinks { private readonly SymbolTable?[] tables = new SymbolTable?[2]; public SymbolTable? this[MembersOrExportsResolutionKind kind] {get; set;} }` |
| 19 | `typeAliasLinks`(687) | `TypeAliasLinks`（types.go:232-237） | declaredType / typeParameters / **instantiations `Dictionary<CacheHashKey,Type>?`（惰性）** / isConstructorDeclaredProperty |
| 20 | `declaredTypeLinks`(688) | `DeclaredTypeLinks`（types.go:241-247） | declaredType / interfaceChecked / indexSignaturesChecked / typeParametersChecked / enumChecked |
| 21 | `spreadLinks`(689) | `SpreadLinks`（types.go:287-290） | leftSpread / rightSpread |
| 22 | `varianceLinks`(690) | `VarianceLinks`（types.go:294-296） | variances `VarianceFlags[]` |
| 23 | `ReverseMappedSymbolLinks`(691，Go 导出大写) | `ReverseMappedSymbolLinks`（types.go:211-215） | propertyType / mappedType / constraintType；C# 字段名照 C# 惯例 `reverseMappedSymbolLinks`，公开只读属性 `ReverseMappedSymbolLinks`（emitresolver/LS 有跨包访问） |
| 24 | `markedAssignmentSymbolLinks`(692) | `MarkedAssignmentSymbolLinks`（types.go:312-315） | lastAssignmentPos int32 / hasDefiniteAssignment |
| 25 | `symbolContainerLinks`(693) | `ContainingSymbolLinks`（types.go:323-327） | extendedContainersByFile `map[ast.NodeId][]*ast.Symbol` / extendedContainers `*[]*ast.Symbol`（**指向切片的指针**：nil=未算、非 nil 空=算过为空 → C# 建模为 `Symbol[]?` + `bool computed` 或 `Optional<Symbol[]>`，逐案见 05-conventions.md nil vs 空切片规则）/ accessibleChainCache `map[accessibleChainCacheKey][]*ast.Symbol`（键 struct types.go:317-321：useOnlyExternalAliasing bool / location *ast.Node / meaning ast.SymbolFlags → `readonly record struct AccessibleChainCacheKey(bool UseOnlyExternalAliasing, Node? Location, SymbolFlags Meaning)`，Node 成员默认引用相等 = Go 指针语义） |

**SourceFile 键（1 个）**

| # | 字段（行） | V（定义处） | V 字段摘要 |
|---|---|---|---|
| 26 | `sourceFileLinks`(694) | `SourceFileLinks`（types.go:391-403） | typeChecked / unusedChecked / externalHelpersModule / requestedExternalEmitHelpers / **deferredNodes `collections.OrderedSet[*ast.Node]`（插入序集合——checkDeferredNodes 按插入序迭代，确定性承重，→ Ts.Collections.OrderedSet<Node>）** / identifierCheckNodes / localJsxNamespace / localJsxFragmentNamespace / localJsxFactory / localJsxFragmentFactory / jsxFragmentType |

### 5.4 链接类的 C# 形态

全部 25 个链接类型 → `internal sealed class`（`MembersAndExportsLinks` 如上包装数组），字段 internal、无构造器逻辑、字段默认值 = Go 零值。示例：

```csharp
internal sealed class ValueSymbolLinks   // types.go:169-177
{
    internal Type? resolvedType;
    internal Type? writeType;
    internal Symbol? target;
    internal TypeMapper? mapper;
    internal Type? nameType;
    internal Type? containingType;
    internal bool functionOrConstructorChecked;
}
```

调用点直译：`links := c.valueSymbolLinks.Get(symbol); links.resolvedType = t` → `var links = valueSymbolLinks.Get(symbol); links.resolvedType = t;` 逐字对应。

---

<a name="6"></a>
## 6. 驻留缓存全清单与结构键 C# 签名

### 6.1 键的统一规则（主计划 §一.6）

Go 可比较 struct 键 → C# `readonly record struct`：编译器合成 `Equals/GetHashCode/IEquatable<T>`，AOT 安全、零装箱；**引用型成员默认引用相等 = Go 指针比较语义自动保真**。Go map → `Dictionary<K,V>`；`*ast.Symbol`/`*ast.Node`/`*Type` 直接做键的 map → `Dictionary` + `ReferenceEqualityComparer.Instance`；string 键 → ordinal 默认比较器（不设 Culture 比较器，见 07-aot-constraints.md InvariantGlobalization 节）。

### 6.2 类型驻留缓存逐一清单（checker.go:620-661 逐行取证）

| Checker 字段（行） | Go 键类型（定义处） | Go 值 | C# 声明 |
|---|---|---|---|
| `stringLiteralTypes`(620) | `string` | `*Type` | `Dictionary<string, Type>`（ordinal） |
| `numberLiteralTypes`(621) | `jsnum.Number`（float64） | `*Type` | `Dictionary<JsNumber, Type>` —— ⚠ 见 §6.4 浮点键注 |
| `nanType`(622) | —（独立字段，因 NaN 不能做 map 键） | `*Type` | `Type nanType` |
| `bigintLiteralTypes`(623) | `jsnum.PseudoBigInt`（jsnum/pseudobigint.go:10，{negative bool; base10Value string}） | `*Type` | `Dictionary<PseudoBigInt, Type>`；`PseudoBigInt` = `readonly record struct(bool Negative, string Base10Value)` |
| `enumLiteralTypes`(624) | `EnumLiteralKey`（checker.go:102-105：enumSymbol *ast.Symbol; value any） | `*Type` | `readonly record struct EnumLiteralKey(Symbol EnumSymbol, object? Value)` —— ⚠ §6.4 装箱键注 |
| `enumNaNLiteralTypes`(625) | `*ast.Symbol` | `*Type` | `Dictionary<Symbol, Type>`（引用比较器） |
| `indexedAccessTypes`(626) | `CacheHashKey` | `*Type` | `Dictionary<CacheHashKey, Type>` |
| `templateLiteralTypes`(627) | `CacheHashKey` | `*Type` | 同上 |
| `stringMappingTypes`(628) | `StringMappingKey`（checker.go:185-188：s *ast.Symbol; t *Type） | `*Type` | `readonly record struct StringMappingKey(Symbol S, Type T)` |
| `uniqueESSymbolTypes`(629) | `*ast.Symbol` | `*Type` | `Dictionary<Symbol, Type>`（引用） |
| `thisExpandoKinds`(630) | `*ast.Symbol` | `thisAssignmentDeclarationKind`（checker.go:17967，int32 枚举） | `Dictionary<Symbol, ThisAssignmentDeclarationKind>`（引用） |
| `thisExpandoLocations`(631) | `*ast.Symbol` | `*ast.Node` | `Dictionary<Symbol, Node>`（引用） |
| `subtypeReductionCache`(632) | `CacheHashKey` | `[]*Type` | `Dictionary<CacheHashKey, Type[]>` |
| `cachedTypes`(633) | `CachedTypeKey`（checker.go:145-148：kind CachedTypeKind(116-141 共 22 值); typeId TypeId） | `*Type` | `readonly record struct CachedTypeKey(CachedTypeKind Kind, TypeId TypeId)` |
| `cachedSignatures`(634) | `CachedSignatureKey`（checker.go:170-173：sig *Signature; key CacheHashKey） | `*Signature` | `readonly record struct CachedSignatureKey(Signature Sig, CacheHashKey Key)` |
| `undefinedProperties`(635) | `string` | `*ast.Symbol` | `Dictionary<string, Symbol>`（ordinal） |
| `narrowedTypes`(636) | `NarrowedTypeKey`（checker.go:152-157：t,candidate *Type; assumeTrue,checkDerived bool） | `*Type` | `readonly record struct NarrowedTypeKey(Type T, Type Candidate, bool AssumeTrue, bool CheckDerived)` |
| `assignmentReducedTypes`(637) | `AssignmentReducedKey`（checker.go:192-195：id1,id2 TypeId） | `*Type` | `readonly record struct AssignmentReducedKey(TypeId Id1, TypeId Id2)` |
| `discriminatedContextualTypes`(638) | `DiscriminatedContextualTypeKey`（checker.go:199-202：nodeId ast.NodeId; typeId TypeId） | `*Type` | `readonly record struct DiscriminatedContextualTypeKey(NodeId NodeId, TypeId TypeId)` |
| `instantiationExpressionTypes`(639) | `InstantiationExpressionKey`（checker.go:206-209，同上二元组） | `*Type` | `readonly record struct InstantiationExpressionKey(NodeId NodeId, TypeId TypeId)` |
| `substitutionTypes`(640) | `SubstitutionTypeKey`（checker.go:213-216：baseId,constraintId TypeId） | `*Type` | `readonly record struct SubstitutionTypeKey(TypeId BaseId, TypeId ConstraintId)` |
| `reverseMappedCache`(641) | `ReverseMappedTypeKey`（checker.go:220-224：sourceId,targetId,constraintId TypeId） | `*Type` | `readonly record struct ReverseMappedTypeKey(TypeId SourceId, TypeId TargetId, TypeId ConstraintId)` |
| `reverseHomomorphicMappedCache`(642) | `ReverseMappedTypeKey` | `*Type` | 同上（两个独立 Dictionary） |
| `iterationTypesCache`(643) | `IterationTypesKey`（checker.go:228-231：typeId TypeId; use IterationUse(483)） | `IterationTypes`（checker.go:507-511，**值 struct**：yield/return/next *Type） | `Dictionary<IterationTypesKey, IterationTypes>`；`IterationTypes` = `readonly record struct(Type? YieldType, Type? ReturnType, Type? NextType)`（Go 值语义，勿改 class） |
| `markerTypes`(644) | `collections.Set[*Type]` | — | `HashSet<Type>(ReferenceEqualityComparer.Instance)` |
| `unresolvedSymbols`(649) | `string` | `*ast.Symbol` | `Dictionary<string, Symbol>`（ordinal） |
| `errorTypes`(650) | `CacheHashKey` | `*Type` | `Dictionary<CacheHashKey, Type>` |
| `moduleSymbols`(651) | `*ast.Node` | `*ast.Symbol` | `Dictionary<Node, Symbol>`（引用） |
| `symbolTableAliasCache`(653) | `symbolTableID`（symbolaccessibility.go:402-415，uint64，高 3 位 kind） | `[]*ast.Symbol` | `readonly record struct SymbolTableId(ulong Value)` + 工厂静态方法（symbolTableIDFromLocals 等，symbolaccessibility.go:417+ 直译） |
| `classExpressionNameTables`(654) | `ast.NodeId` | `ast.SymbolTable` | `Dictionary<NodeId, SymbolTable>` |
| `tupleTypes`(657) | `CacheHashKey` | `*Type` | `Dictionary<CacheHashKey, Type>` |
| `unionTypes`(658) | `CacheHashKey` | `*Type` | 同上 |
| `unionOfUnionTypes`(659) | `UnionOfUnionKey`（checker.go:161-166：id1,id2 TypeId; r UnionReduction(25510); a CacheHashKey） | `*Type` | `readonly record struct UnionOfUnionKey(TypeId Id1, TypeId Id2, UnionReduction R, CacheHashKey A)` |
| `intersectionTypes`(660) | `CacheHashKey` | `*Type` | `Dictionary<CacheHashKey, Type>` |
| `propertiesTypes`(661) | `PropertiesTypesKey`（checker.go:235-240：typeId TypeId; include TypeFlags; includeOrigin,unresolvedMembers bool） | `*Type` | `readonly record struct PropertiesTypesKey(TypeId TypeId, TypeFlags Include, bool IncludeOrigin, bool UnresolvedMembers)` |

### 6.3 散布在 struct 其余处的键控缓存（完整性登记）

| 字段（checker.go 行） | 键（定义处） | C# 键签名 |
|---|---|---|
| `mergedSymbols`(667) | `*ast.Symbol` → `*ast.Symbol` | `Dictionary<Symbol, Symbol>`（引用） |
| `patternForType`(696) / `contextFreeTypes`(697) | `*Type`→`*ast.Node` / `*ast.Node`→`*Type` | 引用键 Dictionary |
| `cachedArgumentsReferenced`(762) | `*ast.Node` → bool | `Dictionary<Node, bool>` |
| `flowLoopCache`(797) | `FlowLoopKey`（checker.go:252-255：flowNode *ast.FlowNode; refKey CacheHashKey） | `readonly record struct FlowLoopKey(FlowNode FlowNode, CacheHashKey RefKey)` |
| `flowTypeCache`(803) | `*ast.Node` → `*Type` | 引用键 |
| `flowNodeReachable`(806)/`flowNodePostSuper`(807) | `*ast.FlowNode` → bool | `Dictionary<FlowNode, bool>`（引用） |
| `enumRelation`(821) | `EnumRelationKey`（checker.go:109-112：sourceId,targetId ast.SymbolId） | `readonly record struct EnumRelationKey(SymbolId SourceId, SymbolId TargetId)` → `RelationComparisonResult` |
| `nonExistentProperties`(894) | `NonExistentPropertyKey`（checker.go:244-248：propNode *ast.Node; containingType *Type; isUncheckedJS bool）集合 | `HashSet<NonExistentPropertyKey>`，键为 `readonly record struct(Node PropNode, Type ContainingType, bool IsUncheckedJS)` |
| `activeTypeMappersCaches`(889) | `[]map[CacheHashKey]*Type`（与 activeMappers(888) 平行栈） | `List<Dictionary<CacheHashKey, Type>>` |
| 类型体内：`ObjectType.instantiations`（types.go:979）、`TypeAliasLinks.instantiations`（types.go:235）、`ConditionalRoot.instantiations`（types.go:1229） | `CacheHashKey` → `*Type`，**惰性**（Go nil map 读安全；写前 make，如 checker.go:25018-25025 读、外部 make） | `Dictionary<CacheHashKey, Type>?`；Go "nil map 可读不可写"语义按 05-conventions.md map 规则处理（读→`?.GetValueOrDefault`，写点在 Go 有显式 make） |

### 6.4 键语义陷阱（审查清单）

1. **浮点键 NaN**：Go map 的 float64 键用 `==`，NaN≠NaN 永不命中——tsgo 因此单设 `nanType` 字段（checker.go:622）并用 `enumNaNLiteralTypes`（625）绕开 NaN 键。C# `EqualityComparer<double>.Default` 却认为 NaN.Equals(NaN)==true。**规则：C# 侧同样绝不以 NaN 为键**（保持 Go 的调用纪律即可，getNumberLiteralType 的 NaN 分流照抄）；`JsNumber` 的 `Equals` 不得改写为 Go map 语义。±0：Go 与 C# 均把 +0/-0 视为同键（.NET `0.0.Equals(-0.0)`==true 且哈希同）——无分歧。
2. **`EnumLiteralKey.Value` 装箱 any 键**：Go 对 `any` 键比较动态类型+值；C# `EqualityComparer<object>.Default` 走虚 `Equals`——对 boxed string/double/bool 均为值相等且类型不同即不等，语义一致。value 为 NaN 的枚举值走 `enumNaNLiteralTypes`，不进此表（同上条纪律）。
3. **键中的引用型成员**（Symbol/Type/Node/Signature/FlowNode）依赖 record struct 默认的 `EqualityComparer<T>.Default`＝引用相等；**这些类一律不得重载 `Equals`/`GetHashCode`**——列入 05-conventions.md 全局禁令。
4. **迭代序**：以上所有缓存在 Go 中从不以迭代序影响输出（Go map 随机序倒逼上游序无关）。C# Dictionary 呈现插入序，**移植点若出现 `for range` 这些 map，必须按主计划 §一.6 审计规则标注"序无关"或补排序**。本包内已知唯一按序迭代的集合是 `SourceFileLinks.deferredNodes`（OrderedSet，插入序承重，§5.3 #26）与 `typeofType` 构造时的 `slices.Sorted(maps.Keys(typeofNEFacts))`（checker.go:1052，Go 已显式排序）。

---

<a name="7"></a>
## 7. 关系缓存与 relationCount 预算机制

### 7.1 五个关系缓存

字段（checker.go:816-820）、初始化（checker.go:1056-1060）：`subtypeRelation`、`strictSubtypeRelation`、`assignableRelation`、`comparableRelation`、`identityRelation`——全部 **per-checker**，类型 `*Relation`。另有第六个轻量表 `enumRelation`（checker.go:821，`map[EnumRelationKey]RelationComparisonResult`，§6.3）。

`Relation`（relater.go:99-116）：

```csharp
internal sealed class Relation      // relater.go:99-101
{
    private Dictionary<CacheHashKey, RelationComparisonResult>? results;   // 惰性，relater.go:108-110

    public RelationComparisonResult Get(CacheHashKey key) =>              // relater.go:103-105（未命中→零值 None）
        results is null ? RelationComparisonResult.None : results.GetValueOrDefault(key);

    public void Set(CacheHashKey key, RelationComparisonResult result)
    {
        results ??= new Dictionary<CacheHashKey, RelationComparisonResult>();
        results[key] = result;
    }

    public int Size => results?.Count ?? 0;                               // relater.go:114-116
}
```

`RelationComparisonResult` 位标志（relater.go:64-76）：Succeeded/Failed/ReportsUnmeasurable/ReportsUnreliable/ComplexityOverflow/StackDepthOverflow + 两个组合掩码，逐位保值。

### 7.2 relationCount 预算（复杂度护栏，行为承重——溢出会产出诊断）

取证 relater.go：

1. 每次顶层 `checkTypeRelatedToEx` 借出一个 `Relater`（对象池 `freeRelater` 单链，relater.go:2566-2586），并设定预算：**`r.relationCount = (16_000_000 - relation.size()) / 8`**（relater.go:370）——预算与该关系缓存当前体积挂钩；
2. 递归比较中每向关系缓存写入一条结果，预算减一（失败写入 relater.go:3162-3163；maybe 栈批量转正 relater.go:3169-3177 中 3174）；
3. 进入 `recursiveTypeRelatedTo` 时 `relationCount <= 0` → `overflow = true` 返回 False（relater.go:3086-3089）；
4. 顶层检测 overflow：把该键写成 `Failed | ComplexityOverflow`（预算耗尽）或 `Failed | StackDepthOverflow`（栈深触顶），防止重试再爆（relater.go:372-375）；随后报 `Excessive_complexity_comparing_types_0_and_1` 或 `Excessive_stack_depth_comparing_types_0_and_1`（relater.go:379-383）；命中缓存的 overflow 结果在 reportErrors 时重放同一诊断（relater.go:3074-3079）。

配套护栏（同函数族）：`len(sourceStack)==100 || len(targetStack)==100` 触发栈深溢出（relater.go:3103-3106）；`isDeeplyNestedType(…, 3)` 置 ExpandingFlags，双向扩张即判 Maybe（relater.go:3110-3130）；`maybeKeys/maybeKeysSet` 环路假设机制（relater.go:3090-3101, 3107-3109）。

**C# 要点**：`Relater` 做同款对象池（checker 内部单线程，普通字段链即可）；`putRelater` 的"重置但保留已扩容切片"（relater.go:2576-2585）→ 保留 `List<T>` 实例 `Clear()` 复用；诊断消息与写缓存次序逐行保持，否则 conformance errors.txt 的 overflow 用例（如深递归类型对比）会 diff。数值 16,000,000 与除 8、栈深 100、嵌套深 3 均为**行为常量**，禁改。

---

<a name="8"></a>
## 8. CacheHashKey / keyBuilder 移植

### 8.1 类型与依赖

- Go：`type CacheHashKey xxh3.Uint128`（checker.go:17313）+ `IsZero()`（17315-17317）。
- C#：`public readonly record struct CacheHashKey(UInt128 Value) { public bool IsZero => Value == 0; }`（主计划 §一.6 既定）。
- 哈希器：`System.IO.Hashing.XxHash128`（XXH3-128，主计划依赖映射表）。流式 `Append(ReadOnlySpan<byte>)`，取值 `GetCurrentHashAsUInt128()`。
- **保真等级**：checker 缓存键**纯进程内**，不落盘、不进基线（主计划 xxh3 保真分级：承重的是 incremental 的 ComputeHash，不在本包）。因此 Go`xxh3.Uint128{Hi,Lo}` 与 C# `UInt128` 的映射只需自洽；不需要与 Go 产出的键逐位相等。**但键构造逻辑必须与 Go 结构 1:1**——键的等价类划分（哪些输入算同一个键）是行为承重的：划分变粗会错误共享缓存导致错基线，变细只损性能。

### 8.2 keyBuilder（checker.go:17319-17407）

```csharp
internal struct KeyBuilder     // checker.go:17319-17321
{
    private XxHash128 h;       // 惰性 new；Relater/键构造均在 checker 单线程内

    public CacheHashKey Hash() => new(h.GetCurrentHashAsUInt128());          // 17323-17325

    public void WriteByte(byte c) { Span<byte> b = [c]; h.Append(b); }       // 17327-17329

    public void WriteString(string s) =>                                     // 17331-17333，见 §8.3 编码裁定
        h.Append(Encoding.UTF8.GetBytes(s));  // 实作用 stackalloc/ArrayPool 优化，语义如此

    public void WriteInt(long value)   => WriteUInt64LE((ulong)value);       // 17335-17337 → hashWrite64
    public void WriteSymbol(Symbol s)  => WriteUInt64LE(Ast.GetSymbolId(s)); // 17339-17341
    public void WriteType(Type t)      => WriteUInt32LE(t.id);               // 17343-17345 → hashWrite32
    public void WriteNodeId(NodeId id) => WriteUInt64LE((ulong)id);          // 17399-17401
    public void WriteNode(Node? node) { if (node != null) WriteNodeId(Ast.GetNodeId(node)); } // 17403-17407（null 时什么都不写！）

    private void WriteUInt32LE(uint v)  { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); h.Append(b); }
    private void WriteUInt64LE(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); h.Append(b); }
}
```

**小端序**：Go 的 `hashWrite32`/`hashWrite64`（checker.go:17289-17311）手工按 `byte(v), byte(v>>8), …` 写出——**小端**。C# 用 `BinaryPrimitives.Write*LittleEndian` 一一对应。虽然进程内自洽不要求端序一致，仍规定照写小端：直译零心智负担，且未来若引入跨进程键比对不再返工。

组合方法直译：`writeTypes`（17347-17352：长度前缀 + 逐个 id）、`writeAlias`（17354-17362：1/0 存在标签 + symbol + typeArguments）、`writeGenericTypeReferences`（17364-17397：'=' 写类型参数驻留索引、深度 <4 时 '<'…'>' 递归、'-' 写普通类型、',' 分隔 source/target，返回 constrained 布尔）。

### 8.3 WriteString 编码裁定

Go `WriteString` 哈希的是 Go 字符串的 **UTF-8 字节**，且 `getTemplateTypeKey`（17502-17514）先写各段 `len(s)`（UTF-8 字节长）再写内容。**裁定：C# 严格 1:1 用 UTF-8**（`Encoding.UTF8`，长度也用 `GetByteCount`）。理由：(a) 逐函数直译零审计成本；(b) 键构造点极少、串极短（模板字面量文本），转码开销可忽略；(c) 避免"进程内单射即可"这类论证扩散成惯例。若阶段 10 profiling 证明此处热，再评估 UTF-16 直写（届时需证明所有 WriteString 调用点两两同编码即可保持等价类不变），并回写本节。

### 8.4 SignatureKey 哨兵常量（checker.go:175-181）

```csharp
internal static class SignatureKeys
{
    public static readonly CacheHashKey Erased    = OneShot("-");   // checker.go:176
    public static readonly CacheHashKey Canonical = OneShot("*");   // 177
    public static readonly CacheHashKey Base      = OneShot("#");   // 178
    public static readonly CacheHashKey Inner     = OneShot("<");   // 179
    public static readonly CacheHashKey Outer     = OneShot(">");   // 180
    private static CacheHashKey OneShot(string s) =>
        new(XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(s)));
}
```

语义：这五个值作为 `CachedSignatureKey.key` 的**特殊哨兵**与真实类型表键（keyBuilder 产物，至少含 8 字节长度前缀）共存于同一 `cachedSignatures` 字典——正确性依赖 xxh3 无碰撞（Go 同样如此），C# 论证同构。一次性 `HashToUInt128` 与流式 `Append+GetCurrentHashAsUInt128` 对同一输入等值（XXH3 规范性质），故哨兵与流式键可比。

### 8.5 键构造函数全清单（供移植核对，行号已核验）

| 函数 | checker.go 行 | 标签字节 / 结构 |
|---|---|---|
| `getTypeListKey` | 17409-17413 | writeTypes |
| `getAliasKey` | 17415-17419 | writeAlias |
| `getUnionKey` | 17421-17443 | origin==nil→types；Union→`'\|'`+origin.Types；Intersection→`'&'`；Index→`'#'`+origin+`'\|'`+types；default panic；尾 writeAlias |
| `getIntersectionKey` | 17445-17454 | types + （无 NoConstraintReduction→writeAlias，否则 `'*'`） |
| `getTupleKey` | 17456-17477 | 每元素 `'#'/'?'/'.'/'*'`（Required/Optional/Rest/Variadic）+ labeledDeclaration nodeId；readonly→`'!'` |
| `getTypeAliasInstantiationKey` | 17479-17481 | → getTypeInstantiationKey(…, false) |
| `getTypeInstantiationKey` | 17483-17491 | types + alias + singleSignature→`'!'` |
| `getIndexedAccessKey` | 17493-17500 | objectType + indexType + hashWrite32(accessFlags) + alias |
| `getTemplateTypeKey` | 17502-17514 | types + `'\|'` + 各段长度 + `'\|'` + 各段内容 |
| `getConditionalTypeKey` | 17516-17524 | types + alias + forConstraint→`'!'` |
| `getRelationKey` | 17526-17542 | isIdentity 且 source.id>target.id 先交换；泛型引用双方→`'g'`+writeGenericTypeReferences，否则 `'s'`+两 id；尾 hashWrite32(intersectionState)；返回 (key, constrained) 二元组 → C# `(CacheHashKey, bool)` 元组或 out 参数 |
| `getNodeListKey` | 17544-17551 | 长度 + 逐 nodeId |

辅助判定 `isTypeReferenceWithGenericArguments`（17553-17557）/`isNonDeferredTypeReference`（17559-17561）随键文件（§4.2 #2/#12 边界）。

### 8.6 键中 id 的确定性说明

键内嵌三种 id：`TypeId`（per-checker 单调计数）、`SymbolId`/`NodeId`（**进程级原子惰性分配**，ast/utilities.go:18-44——多 checker 并发首次触摸同一 Symbol/Node 时用 CAS 抢注，跑与跑之间取值可不同）。这不破坏确定性，因为：所有含这些 id 的键只进 per-checker 内存缓存，**从不序列化、从不决定任何输出顺序**（§6.4 迭代序审计）。C# 侧 `GetNodeId`/`GetSymbolId` 用 `Interlocked.CompareExchange` 直译（02-ast-design.md），本包只消费。

---

<a name="9"></a>
## 9. 并发与确定性

### 9.1 模型（原样移植，主计划 §一.2 既定）

- **N 个 Checker，默认 4**：`checkerCount = 4`；单线程模式 =1；`Options().Checkers` 可覆写；夹取 `max(min(checkerCount, len(files), 256), 1)`（compiler/checkerpool.go:41-48）。
- **每 checker 一把锁**：`NewChecker` 返回 `(*Checker, *sync.Mutex)`（checker.go:902, 1115）；池持 `locks []*sync.Mutex`（checkerpool.go:30）→ C#：`NewChecker` 返回 `(Checker, System.Threading.Lock)`。锁保护的是**外部调用者互斥**（LSP 并发请求）；checker **内部无任何并发**。
- **文件静态钉扎**：`fileAssociations[file] = checkers[i % checkerCount]`（checkerpool.go:114-117）——给定文件序（Program 文件序是确定的）分派完全确定。`createCheckers` 由 `sync.Once` 守护、用 WorkGroup 并行构造 N 个 checker（checkerpool.go:98-119）→ C#：`LazyInitializer`/自写 `Once` + WorkGroup（05-conventions.md 原子/锁映射表）。
- **Ts.LanguageService 侧**的 `CheckerPool` 接口（checkerpool.go:14-22，请求作用域获取/归还）在阶段 8 移植；本阶段只需 compiler 池。

### 9.2 forEachCheckerGroupDo 有序归并（checkerpool.go:145-165 + program.go:567-602）

并行检查的**全部确定性**由三个机制叠加保证，移植时逐条落实：

1. **每 checker 一个任务，任务内按原始文件序过滤自己的文件**：`for i, file := range files { if p.checkers[checkerIdx] == p.fileAssociations[file] { cb(checker, i, file) } }`（checkerpool.go:157-161）。同一 checker 的文件处理次序 = 全局文件序的子序列 ⇒ 每个 checker 内部的类型构造、TypeId 分配、诊断产生次序都与并行调度无关。
2. **槽位化归并**：结果写入 `diagnostics[fileIndex]`（program.go:584-586）——按文件索引定址，无追加竞态、无归并顺序问题。C#：`Diagnostic[][] diagnostics = new[files.Length][]` 槽位数组，各任务只写自己的槽。
3. **终排序**：拼接后 `SortAndDeduplicateDiagnostics`（program.go:575-577；全局诊断同法 checkerpool.go:136-143）——比较器基于文件名/位置/code/消息文本的全序 + 去重。排序是稳定语义的最后一道闸。

### 9.3 为何"4 并行 ≡ 单线程"输出全等（证明义务）

论证链（每条都是移植时的不变量，破坏任何一条都表现为并行/串行 diff）：

1. 文件→checker 分派是纯函数（i % N，§9.1）；单线程模式只是 N=1 的特例 + WorkGroup 串行执行；
2. **checker 之间零共享可变状态**：`Type.checker` 反向指针保证类型不跨界（§1.1）；全部缓存/LinkStore/关系缓存 per-checker（§5-§7）；每个 checker 各自 `initializeChecker` 合并出**自己的** `c.globals`/`mergedSymbols`（checker.go:930, 972）；跨 checker 只读共享的是 binder 产物（AST/Symbol），其上唯一的写是 NodeId/SymbolId 的 CAS 惰性分配（§8.6）——值可变但**从不进入输出**；
3. checker 内部执行序列由（该 checker 的文件子序列 × 确定性代码）唯一决定 ⇒ 每个文件产出的诊断集合与串行时逐字节相同；
4. 归并按槽位 + 终排序（§9.2）消除任务完成次序的影响。

**遗留风险源（C# 特有，测试必须覆盖）**：Dictionary 插入序泄漏（Go 随机序在 tsgo 侧已把这类 bug 灭活，C# 会复活它们——§6.4 审计规则）；`checkSourceFile` 内 `ctx` 取消时序（wasCanceled，checker.go:615）在门禁运行中不启用取消即可。

### 9.4 如何测（阶段 5 出口门禁的确定性部分，详见 08-testing-strategy.md / 09-phases.md〔补写中，R20〕）

1. **并行=串行**：整套 conformance 以 `--singleThreaded`（WorkGroup 串行开关，主计划 §四.3）与默认 4-checker 各跑一遍，全部 .errors.txt/.types/.symbols 基线产物逐字节 diff==0；
2. **对抗性 N**：加一档 `checkers=7`（与文件数不整除、非 2 幂）复跑抽样语料，捕捉对"N=4 恰好整除"的隐性依赖；
3. **重复运行稳定性**：同配置连续跑 2 次 diff==0，捕捉 Dictionary 序/id 分配序泄漏（NodeId CAS 次序跨 run 不同，若有泄漏此测必红）；
4. **LSP 互斥语义**（阶段 8）：并发请求下锁的获取/释放（`sync.OnceFunc` 的一次性 release，checkerpool.go:69-71 → C# `Interlocked` 一次性标志）单元测试。

---

<a name="10"></a>
## 10. 递归深度策略

### 10.1 已知深递归函数族（取证）

| 函数族 | 取证 | 深度来源 |
|---|---|---|
| `checkExpression`/`checkExpressionEx`（checker.go:7530-7534）及整个表达式树下行 | `checkBinaryLikeExpression` 对左右操作数**直接递归** `checkExpressionEx`（checker.go:12303-12304）——tsgo **没有** strada 的 binary-expression trampoline | 左结合长链（`a+b+c+…` 数千项）深度 = 链长；conformance 含此类压力用例 |
| `getTypeFromTypeNode`（checker.go:22713）族 | 类型节点结构递归（union/交叉/条件/模板嵌套） | 类型嵌套深度 |
| 关系比较 `isRelatedToEx → structuredTypeRelatedTo → …`（relater.go:2588-3167） | 语义深度封顶 100（relater.go:3103），但**每层语义深度对应几十个原生栈帧**（成员/签名/索引逐项互递归） | 100 × 每层帧数 × 帧大小 |
| `instantiateType` 族（checker.go:22005-22047） | 语义守卫 100/5M（22016），同样每层多帧 | 深泛型展开 |
| `checkSourceElement` 语句递归（checker.go:2229+）、`getUnionTypeWorker`/`getIntersectionType` 构造递归、约束计算（27302+）、条件类型分布 | 结构递归 | 源码嵌套 |
| NodeBuilder 序列化（nodebuilderimpl.go） | `serializationLevel < maxSerializationLevel(=2)`（checker.go:546, 13913-13920）限制的是重入次数而非栈深 | 类型打印递归 |

Go 协程栈按需增长到 GB 级，上述统统无恙；.NET 线程栈固定（默认 ~1MB），**StackOverflowException 不可捕获、进程直接死**——这就是主计划的 0 号风险。

### 10.2 LargeStackExecutor（Ts.Core，主计划 §一.5 既定；本节规定 checker 侧的使用契约）

- 机制：所有 parse/bind/check 入口跑在 **64MB 专用大栈线程**（`new Thread(entry, maxStackSize: 64 * 1024 * 1024)`），对应 tsc.js 的 `--stack-size`。`WorkGroup` 的并行与串行两个实现共用它——即 §9 的 checker 任务线程**天然就是大栈线程**。
- **checker 侧契约**：
  1. `checkerPool.createCheckers`（含 `NewChecker → initializeChecker`，会解析 lib 全局类型，可深递归）与 `forEachCheckerGroupDo` 的任务体必须经 WorkGroup 派发（Go 侧本来如此，checkerpool.go:101-112, 152-163），禁止绕过直接 `Task.Run`/线程池；
  2. LSP/API 入口（阶段 8/9）对 checker 的每次调用同样必须经 LargeStackExecutor 蹦床进入；
  3. **checker 全域禁用 `async/await`**（await 恢复可能落回小栈线程池线程）；
  4. 递归函数**不做手工去递归/迭代化改写**——保持与 Go 逐行对照，栈问题统一由大栈线程解决（唯一例外：若某具体用例在 64MB 下仍溢出，先在 12-risks.md 记录，经评审后才允许对该函数做显式栈改写并在两侧加注释锚点）。
- **验证**：阶段 5 门禁加"栈金丝雀"测试——选取 conformance 中最深的表达式/类型嵌套用例（如数千项二元链、深嵌套条件类型），(a) 在默认 1MB 线程上跑、**预期溢出**（借子进程隔离观察退出码，确认风险真实、金丝雀有效）；(b) 在 LargeStackExecutor 上跑、必须通过。AOT 发布物复跑（b）。

### 10.3 既有语义护栏清单（这些是**行为**，不是可调参数；溢出路径产出诊断，进 errors.txt 基线）

| 护栏 | 常量 | 取证 | 触发效果 |
|---|---|---|---|
| 实例化深度 | `instantiationDepth == 100` | checker.go:22016 | 报 instantiation excessively deep，返回 errorType |
| 实例化总量 | `instantiationCount >= 5_000_000` | checker.go:22016 | 同上 |
| 条件约束深度 | `conditionalConstraintDepth >= 100` | checker.go:27429-27434 | 返回 nil 约束 |
| 关系预算 | `(16M − size)/8`，每写一条减 1 | relater.go:370, 3086, 3163, 3174 | Excessive_complexity 诊断（§7.2） |
| 关系栈深 | source/targetStack == 100 | relater.go:3103 | Excessive_stack_depth 诊断 |
| 深嵌套判定 | `isDeeplyNestedType(…, 3)` | relater.go:3113/3119 | ExpandingFlagsBoth → Maybe |
| 条件类型尾递归 | `tailCount == 1000` | checker.go:24218 | 中止展开 |
| 并集规模 | 100,000 / 估算 >1,000,000 | checker.go:25880-25886 | 报错并截断 |
| 交叉展开规模 | `size >= 100_000` | checker.go:26521 | 报错 |
| 类型解析环检测 | `typeResolutions` 栈 + `resolutionStart`（字段 787-788） | pushTypeResolution 机制 | 环 → 报循环性错误 |
| 序列化重入 | `maxSerializationLevel = 2` | checker.go:546, 13913 | 停止嵌套序列化 |
| flow 相关 | `flowLoopStack`/`sharedFlows`/`flowInvocationCount`（字段 798-802） | flow.go | 循环流缓存 |

这些护栏保证语义深度有界，但**不保证原生栈安全**（§10.1 第 3-4 行），两者叠加（护栏 + 64MB 大栈）才构成完整策略。

---

<a name="11"></a>
## 11. Symbol / Signature / IndexInfo / TypeMapper / FlowNode 类设计

### 11.1 Symbol（Ts.Ast；ast/symbol.go:10-21）

```csharp
public sealed class Symbol
{
    public SymbolFlags Flags;
    public CheckFlags CheckFlags;        // 仅 checker 造的 transient symbol 非零（ast/symbol.go:12 注释）
    public string Name = "";
    public List<Node>? Declarations;     // Go []*Node
    public Node? ValueDeclaration;
    public SymbolTable? Members;
    public SymbolTable? Exports;
    internal ulong id;                   // 惰性，Interlocked.CompareExchange（ast/utilities.go:34-44）
    public Symbol? Parent;
    public Symbol? ExportSymbol;
}
```

- 归属 `Ts.Ast`（Go 中在 ast 包），checker 是主要生产者之一：`c.newSymbol`（`symbolArena` checker.go:664 删除后即 `new Symbol`）。
- **禁止重载 Equals/GetHashCode**（§6.4）；所有以 Symbol 为键的容器用引用比较器。
- `ast.SymbolTable` = `map[string]*ast.Symbol` → `Dictionary<string, Symbol>`（ordinal）；迭代点逐案审计（§6.4）。
- checker 的知名符号字段：undefinedSymbol/argumentsSymbol/requireSymbol/unknownSymbol（checker.go:645-648, 构造 955-958）、globalThisSymbol（962-964，注意 `Exports = c.globals` 的自引用接线）。

### 11.2 Signature 与 TypePredicate（Ts.Checker；types.go:1276-1348）

```csharp
public sealed class Signature            // types.go:1276-1291
{
    internal SignatureId id;
    internal SignatureFlags flags;
    internal int minArgumentCount;               // Go int32
    internal int resolvedMinArgumentCount;       // Go int32；0 义为未解析？否——Go 用独立字段+哨兵逻辑，照抄
    internal Node? declaration;
    internal Type[]? typeParameters;
    internal Symbol[] parameters = [];
    internal Symbol? thisParameter;
    internal Type? resolvedReturnType;
    internal TypePredicate? resolvedTypePredicate;
    internal Signature? target;
    internal TypeMapper? mapper;
    internal Type? isolatedSignatureType;
    internal CompositeSignature? composite;
    // 公开 accessor（types.go:1293-1327）→ 属性
}

public sealed class CompositeSignature   // types.go:1329-1332
{
    internal bool isUnion;
    internal Signature[] signatures = [];
}

public sealed class TypePredicate        // types.go:1343-1348
{
    internal TypePredicateKind kind;     // int32 枚举（types.go:1334-1341）
    internal int parameterIndex;
    internal string parameterName = "";
    internal Type? t;                    // 字段名 t 照 Go；公开属性 Type
}
```

- `signatureArena`（checker.go:665）删除 → `c.newSignature` 直接 `new`；`SignatureId` 由 `c.SignatureCount` 分配（per-checker）。
- 知名单例签名：anySignature/unknownSignature/resolvingSignature/silentNeverSignature（checker.go:758-761，构造 1042-1045）——`resolvingSignature` 以**引用同一性**做"解析中"哨兵，C# 保持同款对象比较。
- `noTypePredicate` 哨兵（checker.go:757，构造 1041：parameterName 为 `"<<unresolved>>"`）。

### 11.3 IndexInfo（Ts.Checker；types.go:1368-1375）

```csharp
public sealed class IndexInfo
{
    internal Type keyType = null!;
    internal Type valueType = null!;
    internal bool isReadonly;
    internal Node? declaration;      // IndexSignatureDeclaration
    internal Symbol? indexSymbol;
    internal Node[]? components;     // ElementWithComputedPropertyName
}
```

`indexInfoArena`（checker.go:666）删除。两个单例：`enumNumberIndexInfo`/`anyBaseTypeIndexInfo`（checker.go:763-764，构造 1047-1048 对象初始化器直译）。IndexInfo 在成员解析中以引用被切片共享，**不得**改为 struct。

### 11.4 TypeMapper（Ts.Checker；mapper.go 全文 315 行）

Go 侧是与 Type 完全同款的 header+data 侵入式模式（`TypeMapper{data TypeMapperData}`，`TypeMapperBase` 内嵌 + `m.data = m` 指回自身，mapper.go:22-36, 87-93）。**同法坍缩**：

```csharp
public abstract class TypeMapper
{
    // TypeMapperBase 的默认实现（mapper.go:91-93）上提为虚方法默认体：
    public virtual Type Map(Type t) => t;
    public virtual TypeMapperKind Kind => TypeMapperKind.Unknown;
    public virtual bool MapsThisOnly => false;
}
```

8 个具体子类（全 sealed，工厂函数直译为静态方法/Checker 方法）：

| 子类 | mapper.go 行 | 关键语义 |
|---|---|---|
| `SimpleTypeMapper` | 97-124 | 单对替换；`MapsThisOnly` 看 isThisTypeParameter |
| `ArrayTypeMapper` | 128-157 | 平行数组线扫（**保持线扫**，勿改字典——n 极小且引用比较） |
| `ArrayToSingleTypeMapper` | 161-184 | 多源→单目标（推断默认值用） |
| `DeferredTypeMapper` | 188-213 | targets 为 `Func<Type>[]` 惰性 |
| `FunctionTypeMapper` | 217-231 | 包装 `Func<Type,Type>`；Checker 上五个单例：uniqueLiteralMapper/reportUnreliableMapper/reportUnmeasurableMapper/restrictiveMapper/permissiveMapper（字段 735-740，构造 1016-1020） |
| `MergedTypeMapper` | 235-255 | `m2.Map(m1.Map(t))` |
| `CompositeTypeMapper` | 259-281 | 持 Checker；`m1` 命中则 `instantiateType(t1, m2)`——与 Merged 语义**不同**，两者并存 |
| `InferenceTypeMapper` | 285-315 | 持 InferenceContext；fixing 副作用（inferFromIntraExpressionSites + isFixed）逐行直译 |

工厂组合函数：`newTypeMapper`（40-45）、`combineTypeMappers`（47-52，Checker 方法）、`mergeTypeMappers`（54-59）、`prependTypeMapping`/`appendTypeMapping`（61-73）、`newBackreferenceMapper`（77-83）。

注意 Go 工厂返回 `&m.TypeMapper`（内嵌头指针）而 C# 直接返回对象——调用点无差异。`Type.Mapper()` 分派见 §1.3。

### 11.5 FlowNode 与流分析状态（Ts.Ast + Checker.Flow.cs）

```csharp
// Ts.Ast（ast/flow.go:27-37）
public sealed class FlowNode
{
    public FlowFlags Flags;
    public Node? Node;             // 关联 AST 节点
    public FlowNode? Antecedent;   // 非 Label 的单前驱
    public FlowList? Antecedents;  // Label 的前驱链表
}
public sealed class FlowList { public FlowNode Flow = null!; public FlowList? Next; }
// FlowLabel = FlowNode 别名（ast/flow.go:39）→ global using FlowLabel = Ts.Ast.FlowNode;
```

- `FlowList` **保持手写单链表**（binder 共享尾部构造多前驱图），不得替换为 `List<T>`。
- Checker 侧流状态（字段 checker.go:795-807）：`freeFlowState`/`freeinferenceState` 对象池（与 Relater 同款单链复用）、`flowLoopCache`/`flowLoopStack`/`sharedFlows`/`antecedentTypes`、`flowAnalysisDisabled`、`flowInvocationCount`、`flowTypeCache`、`lastFlowNode(Reachable)` 双字段微缓存、`flowNodeReachable`/`flowNodePostSuper` 引用键字典（§6.3）。`SharedFlow` struct 在 flow.go:35。
- FlowNode 以引用同一性为键（`FlowLoopKey` §6.3）——同样禁止重载 Equals。

---

<a name="12"></a>
## 12. 待澄清

以下事项与主计划存在措辞出入或主计划未裁定，**本文未擅改设计**，按源码实测执行并在此登记，待主计划维护者裁决后同步：

1. **"21 类型表" vs 24**：主计划 §三 对 03 号文档的描述写"21 类型表"，而 §一.2 与对抗验证结论均为 **24 struct（20 具体 + 4 抽象）**。本文按 24 执行（types.go 逐行核验，§1.2）；"21"疑为文档集条目笔误。
2. **LinkStore 数量 "~30" vs 实测 26**：checker.go:669-694 实测 26 个 LinkStore 字段、25 个链接数据类型（§5.3 全清单）。"~30"为约数；以本文清单为准，若上游追加（如 jsx 之外的新 links）在 11-progress.md 登记增量。
3. **keyBuilder.WriteString 编码**：主计划只规定 CacheHashKey"XxHash128.Append 流式构建 1:1"，未裁定字符串编码。本文裁定严格 UTF-8 1:1（§8.3）并允许阶段 10 经证明后放宽为 UTF-16。若 05-conventions.md 对"进程内哈希键的字符串编码"另有全局裁定，以其为准并回写 §8.3。
4. **"驻留缓存清单（checker.go 626-660）"行号范围**：任务描述的行段与实际字段跨度不完全吻合——字面量/驻留缓存实起于 620（stringLiteralTypes）且相关键控缓存散布至 661/696/797/821/894。本文按**全字段实测**覆盖（§6.2-§6.3），超出指定行段的部分一并列出以保完整性。
5. **`Checker.mu` 与 `emitResolverOnce`**（checker.go:898, 882）：`sync.Mutex` → `System.Threading.Lock`、`sync.Once` → C# Once 助手的全局映射归 05-conventions.md；本文仅登记本包使用点（§9.1、EmitResolver 惰性构造）。EmitResolver 本体设计随阶段 6（emit）文档细化。
6. **`ReverseMappedSymbolLinks` 字段的 Go 导出大小写**（checker.go:691 是 26 字段中唯一大写导出者）：本文按 C# 惯例统一为私有字段 + 公开属性（§5.3 #23）；若跨包（emitresolver/ls）访问面要求不同的公开粒度，在阶段 8 复核。
7. **types.go:743 的不可达 case**（`Type.Target()` 中 Mapped 分支被 735 行 Object case 短路，§1.3）：本文选择逐字直译保留。若上游 tsgo 后续修复，随上游钉扎升级流程（00-overview.md）同步。
8. **types.go 行数**（已裁决 R21）：主计划写 types.go「~2,700 行」，实测 **1,459 行**（`wc -l` 核验），主计划为笔误；本文全文（总原则 4、§4.1 表）按实测 1,459 行执行。
