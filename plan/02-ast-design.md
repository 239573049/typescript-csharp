# 02 — AST 的 C# 设计

| 元信息 | |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-07-11 |
| 状态 | Draft |
| 上游主计划 | ~/.claude/plans/precious-riding-hoare.md §一.1（AST 节点表示）、§五（对抗验证结论第 1 条） |
| 本档职责 | AST 层的全部 C# 设计：继承树、mixin/brand 分类、生成器三规则精确算法、Node 抽象类成员规格、mixin 虚访问器机制、真实调用点对照、NodeFactory/NodeList/SourceFile、类型别名、nil 接收者、内存布局 |
| 交叉引用 | 01-architecture.md（线程/内存模型）、03-checker-design.md（Type 层次）、04-codegen.md（生成器工程化）、05-conventions.md（Go→C# 惯用法）、07-aot-constraints.md、08-testing-strategy.md、12-risks.md |

**证据源（本档所有论断的出处）**：

| 文件 | 行数 | 作用 |
|---|---|---|
| `typescript-go-main/_scripts/ast.json` | 5,487 | 唯一 schema：35 个 base + 192 个节点定义 + 72 个 union/base 别名 + 23 个 list 别名 + 351 个 kind 元素 + 34 个 kind marker + 34 个 kind alias |
| `typescript-go-main/_scripts/schema.ts` | 1,267 | schema 解析/类型解析 API（`SchemaAPI`），Go/TS 生成器共用 |
| `typescript-go-main/_scripts/generate-go-ast.ts` | 1,028 | Go 生成器（我们要移植成 generate-csharp-ast.ts 的蓝本） |
| `typescript-go-main/internal/ast/ast.go` | 3,059 | 手写部分：Node/NodeDefault/NodeList/ModifierList/SourceFile/switch 型访问器/mixin 访问器实现 |
| `typescript-go-main/internal/ast/ast_generated.go` | 10,047 | 生成部分：base struct、192 节点 struct、193 工厂、166 Update、191 Clone、167 ForEachChild、166 VisitEachChild、192 As*、232 Is*、kind 守卫 |
| `typescript-go-main/internal/ast/utilities.go` / `ids.go` / `visitor.go` / `parseoptions.go` | — | GetNodeId、NodeVisitor、SourceFileParseOptions |

**数字总账**（以下全部为对上述文件的程序化统计，本档编写时逐项核验）：

- 35 base = **18 个 Node 系** + **13 个数据 mixin** + **4 个无字段 brand**
- 192 个节点定义；其中 1 个 handWritten（SourceFile）、1 个 handWrittenVisitor（JSDocParameterOrPropertyTag）、2 个多 Kind 节点（TypeAliasDeclaration/JSTypeAliasDeclaration、ImportDeclaration/JSImportDeclaration）、3 个带类型参数的节点（Token、KeywordExpression、KeywordTypeNode）、46 个 arena 节点、38 个 generateSubtreeFacts 节点
- 生成面：As* ×192、New* ×193、Update* ×166、Clone ×191、ForEachChild ×167、VisitEachChild ×166、Is* ×232、arena 字段 ×48（46 节点 + modifierList + nodeList）
- 别名：节点别名 `XxxNode = Node` ×192、实例化别名 ×39（Token 33 + KeywordExpression 6）、list 别名 ×23、union/base 别名 ×72、ast.go 手写别名 ×7、kind 别名 ×34

---

## 1. Go 原版形态与 C# 坍缩原理（为什么是密封类继承）

### 1.1 Go 的 header+data 结构不是两次分配

Go 的 `Node`（ast.go:179-186）：

```go
type Node struct {
    Kind   Kind             // int16（kind_generated.go: type Kind int16）
    Flags  NodeFlags        // uint32（nodeflags.go:3）
    Loc    core.TextRange   // struct{pos, end TextPos}，TextPos=int32（core/text.go:5-12）
    id     atomic.Uint64
    Parent *Node
    data   nodeData         // 接口，16 字节，指回自身
}
```

每个具体节点 struct 通过嵌入链把这个头**内联**在同一分配中：`Identifier` → `PrimaryExpressionBase` → … → `NodeBase` → `NodeDefault` → `Node`（ast_generated.go:620-624、ast.go:1206-1251）。工厂里 `newNode(kind, data)` 做的是 `n := data.AsNode(); n.data = data`（ast.go:74-83）——**data 接口指回包含它自己的那个对象**。所以 header+data 拆分只是 Go 缺继承的补偿：`nodeData` 是一张手工虚表，`NodeDefault` 是默认实现基类，`n.data.(*Identifier)` 是向下转型。

### 1.2 C# 的直接表达

C# 类继承直接给出同一内存形状且省掉 16 字节 data 指针：

- `nodeData` 接口（21 个方法，ast.go:1181-1202）→ Node 上的 **~20 个虚方法**（`AsNode()` 退化为 `this`，见 §7.2）
- `NodeDefault`（ast.go:1206-1245）→ 虚方法的**默认实现体**，併入 Node 本身
- `NodeBase`（ast.go:1249-1251，只嵌 NodeDefault）→ 同样併入 Node；因此 18 个 Node 系 base 在 C# 中是 **Node + 17 个抽象类**
- `n.data.(*Identifier)` 型断言（panic on failure）→ `(Identifier)this` 强转（InvalidCastException 对应 panic）
- Go 方法提升（embedding promotion）→ C# override；Go 提升的歧义/平局问题（§6.2、§6.4）在 C# 中自然消失

```csharp
namespace Ts.Ast;

public abstract partial class Node
{
    public SyntaxKind Kind;          // enum SyntaxKind : short（对应 Go Kind int16）
    public NodeFlags Flags;          // [Flags] enum NodeFlags : uint
    public TextRange Loc;            // readonly-able struct { int Pos; int End; }，UTF-16 code unit 偏移
    internal ulong id;               // 0 = 未分配；GetNodeId 用 Interlocked.CompareExchange 惰性分配
    public Node? Parent;
    // 其余成员见 §7
}
```

> 位置语义：`Loc` 全程为 **UTF-16 code unit** 偏移（主计划 §一.6 UTF-8→UTF-16 决策，PositionMap 删除；见 §12 SourceFile 与 01-architecture.md）。

---

## 2. 35 个 base 的三分类（对 ast.json 程序化核验）

分类判据（生成器实现同一算法）：

1. **Node 系**：extends 传递闭包能到达 `NodeBase` → C# 抽象类单继承链；
2. **数据 mixin**：到不了 NodeBase 且自带字段（或 extends 里引入字段）→ C# 接口 + 字段扁平化 + Node 虚访问器；
3. **brand**：到不了 NodeBase 且无任何字段 → C# 纯标记接口。

结果：18 + 13 + 4 = 35，与主计划一致。三类之间**血缘零交叉**（没有任何 mixin/brand 嵌入 Node 系 base；Node 系 base 可以嵌入 mixin/brand，方向是单向的）。

### 2.1 Node 系 18 个：继承树全图

Go 深度（`NODE-LINEAGE DEPTHS` 程序化输出）与 C# 继承关系如下。**C# 中 `NodeBase` 併入 `Node`，其余 17 个为 `abstract class`：**

```
Node                                     (= Go Node + NodeDefault + NodeBase 三合一，手写)
├── StatementBase                        extends [NodeBase, FlowNodeBase]     brand _statementBrand
│   └── IterationStatementBase           extends [StatementBase]              字段 Statement: *Statement
├── ExpressionBase                       extends [NodeBase]                   brand _expressionBrand
│   └── UnaryExpressionBase              brand _unaryExpressionBrand
│       └── UpdateExpressionBase         brand _updateExpressionBrand
│           └── LeftHandSideExpressionBase   brand _leftHandSideExpressionBrand
│               └── MemberExpressionBase     brand _memberExpressionBrand
│                   └── PrimaryExpressionBase brand _primaryExpressionBrand
│                       └── LiteralExpressionBase extends [LiteralLikeNodeBase, PrimaryExpressionBase]
│                                                 brand _literalExpressionBrand（携带 LiteralLikeNodeBase mixin）
├── TypeNodeBase                         extends [NodeBase, TypeSyntaxBase]   brand _typeNodeBrand
│   ├── NodeWithTypeArgumentsBase        字段 TypeArguments: *TypeList?
│   ├── JSDocTypeBase                    brand _jsDocTypeBrand
│   ├── FunctionOrConstructorTypeNodeBase extends [TypeNodeBase, ModifiersBase, FunctionLikeBase]
│   └── UnionOrIntersectionTypeNodeBase  字段 Types: *TypeList
├── AccessorDeclarationBase              extends [NamedMemberBase, FunctionLikeWithBodyBase, FlowNodeBase,
│                                                 TypeElementBase, ClassElementBase, ObjectLiteralElementBase,
│                                                 CompositeBase, NodeBase]
├── JSDocTagBase                         extends [NodeBase]  字段 TagName: *IdentifierNode, Comment: *NodeList?
└── JSDocCommentBase                     extends [NodeBase]  字段 text: []string (private)
```

要点：

- **Node 系 base 自己也携带 brand**（`_statementBrand` 等 9 个）。这些 brand 只在 TS 侧做 nominal typing，Go 侧根本不存在（BaseEntry.brand 字段，schema.ts:51-55）。C# 侧**抽象类本身就是名义类型**，这 9 个 brand 无需任何表达。只有 §2.3 的 4 个"无宿主"brand 需要标记接口。
- `LiteralExpressionBase` 与 `AccessorDeclarationBase`、`FunctionOrConstructorTypeNodeBase` 是三个"Node 系 + mixin 混载"的抽象类：它们在 C# 中是链上抽象类，**同时实现其 mixin 接口并容纳扁平化字段**（§6.2）。主计划规则 1 已明确这两个 `*OrConstructor*`/`UnionOrIntersection*` base 因带内容必须是抽象类而非接口。
- `AccessorDeclarationBase` 的 C# 基类是 `Node`（其 extends 中唯一 Node 系项就是 NodeBase）；`GetAccessorDeclaration`/`SetAccessorDeclaration` 继承它。
- Go 侧的 `IsAccessorDeclaration()` 空方法（ast.go:1946）是 Go 的接口 witness，C# 中不需要（类型测试用 `is AccessorDeclarationBase` 或 Kind 判断）。

### 2.2 数据 mixin 13 个：字段清单

「载体数」= 闭包中含该 mixin 的**具体节点**数（192 节点上程序化统计）。字段列自 ast.json bases 与 ast_generated.go:78-244 的生成结果；`goOnly` 表示 TS 侧不存在（Go 专属数据）、`noGo` 表示 Go 侧不存在（本表已剔除）：

| # | mixin | extends（mixin 嵌套） | 自有字段（Go 类型 → C# 类型） | 载体数 | Node 虚访问器 |
|---|---|---|---|---|---|
| 1 | DeclarationBase | — | `Symbol *Symbol` goOnly → `Symbol? Symbol` | 54 | `DeclarationData()` |
| 2 | ExportableBase | — | `LocalSymbol *Symbol` goOnly → `Symbol? LocalSymbol` | 14 | `ExportableData()` |
| 3 | ModifiersBase | — | `modifiers *ModifierList`（private；另有 TS-only `modifierFlags` noGo，不生成） → `ModifierList? modifiers` | 33 | `Modifiers()` / `SetModifiers()` |
| 4 | LocalsContainerBase | — | `Locals SymbolTable` goOnly、`NextContainer *Node` goOnly → `SymbolTable? Locals; Node? NextContainer` | 27 | `LocalsContainerData()` |
| 5 | FlowNodeBase | — | `FlowNode *FlowNode` goOnly → `FlowNode? FlowNode` | 44 | `FlowNodeData()` |
| 6 | CompositeBase | — | `facts atomic.Uint32` goOnly → `private uint facts`（Volatile/Interlocked） | 72 | 无访问器；表现为 `SubtreeFactsWorker()` 缓存 override（§6.4） |
| 7 | FunctionLikeBase | DeclarationBase, LocalsContainerBase | `TypeParameters *TypeParameterList?`、`Parameters *ParameterList`、`Type *TypeNode?`、`FullSignature *TypeNode?` | 14 | `FunctionLikeData()` |
| 8 | BodyBase | — | `AsteriskToken *AsteriskToken?`、`Body *NodeBody?`、`EndFlowNode *FlowNode` goOnly | 8 | `BodyData()` |
| 9 | FunctionLikeWithBodyBase | FunctionLikeBase, BodyBase | 无 Go 侧自有字段（其 `Body: BlockOrExpression?` 是 **noGo** 的 TS 窄化，ast.json 实测；Go struct 只有两个嵌入，ast_generated.go:168-171） | 7 | 无自有访问器（复用 7/8 的） |
| 10 | ClassLikeBase | DeclarationBase, ExportableBase, ModifiersBase, LocalsContainerBase, CompositeBase | `name *IdentifierNode?`（private）、`TypeParameters *TypeParameterList?`、`HeritageClauses *HeritageClauseList?`、`Members *ClassElementList` | 2（ClassDeclaration/ClassExpression） | `ClassLikeData()`（+`Name()` override） |
| 11 | LiteralLikeNodeBase | — | `Text string`、`TokenFlags TokenFlags` | 9 | `LiteralLikeData()` |
| 12 | TemplateLiteralLikeNodeBase | LiteralLikeNodeBase | `RawText string`、`TemplateFlags TokenFlags` | 4 | `TemplateLiteralLikeData()`（+`LiteralLikeData()`） |
| 13 | NamedMemberBase | DeclarationBase, ModifiersBase | `name *PropertyName`（private）、`PostfixToken *TokenNode?` | 9 | 无自有访问器（复用 1/3 的 + `Name()` override） |

### 2.3 brand 4 个 → 纯标记接口

| brand base | C# | 关联 union 别名 |
|---|---|---|
| TypeSyntaxBase | `public interface TypeSyntaxBase {}` | （TypeNode 经 TypeNodeBase 携带；4 个签名声明节点直接携带）——**注意它在 Go 有 2 个手写方法，见 §6.4 的行为再安置** |
| TypeElementBase | `public interface TypeElementBase {}` | `TypeElement = Node // Node with TypeElementBase` |
| ClassElementBase | `public interface ClassElementBase {}` | `ClassElement = Node` |
| ObjectLiteralElementBase | `public interface ObjectLiteralElementBase {}` | `ObjectLiteralElement = Node` |

接口命名**不加 I 前缀**（主计划明确的例外，保 grep 对照性；例外记录归 05-conventions.md）。这些接口的用途：(a) 保留 schema 语义供人查阅与将来 API 编码器（阶段 9）使用；(b) `is` 测试可用（但移植代码一律走 Go 同款 `Is*`/Kind 判定，不新增 `is` 用法）。

---

## 3. 生成器三规则的精确算法

生成器 `tools/generator/generate-csharp-ast.ts` 移植自 generate-go-ast.ts（1,028 行），**原样复用 ast.json + schema.ts 副本**（同步流程见 04-codegen.md）。Go 生成器对 extends 的处理是"无脑全部嵌入"（generate-go-ast.ts:148-151、241-244），歧义交给 Go 编译器 + ast.go 手写 override 解决；C# 单继承没有这个奢侈，因此需要下面三条**显式**规则。

### 3.1 规则 1：基类选取 = extends 中「最派生」的 Node 系 base

算法：

```
输入: 节点/base X 的 extends 列表 E
1. L := E 中属于 Node 系的项（判据 §2 之 1）
2. 断言 L 非空（每个节点/Node 系 base 必须有 Node 血缘）
3. 若 |L| == 1 → 基类 = L[0]
4. 若 |L| > 1 → 要求 L 中存在唯一极大元 M（即 L 中其余项都是 M 的祖先）；
   基类 = M，其余项为冗余重列，丢弃（它们已由 M 继承而来）
5. 若存在两个互不为祖先的项 → 生成器报错终止（schema 出现真菱形，需人工决策）
```

对 192 节点程序化扫描，**恰有 4 个节点触发第 4 步**（与主计划 §五第 1 条 PARTIAL 修正一致）：

| 节点 | extends（ast.json 原文） | 冗余项 | C# 基类 |
|---|---|---|---|
| UnionTypeNode | `["TypeNodeBase","UnionOrIntersectionTypeNodeBase"]` | TypeNodeBase（是 UnionOrIntersectionTypeNodeBase 的父） | `UnionOrIntersectionTypeNodeBase` |
| IntersectionTypeNode | 同上 | 同上 | `UnionOrIntersectionTypeNodeBase` |
| FunctionTypeNode | `["TypeNodeBase","FunctionOrConstructorTypeNodeBase"]` | TypeNodeBase | `FunctionOrConstructorTypeNodeBase` |
| ConstructorTypeNode | 同上 | 同上 | `FunctionOrConstructorTypeNodeBase` |

Go 侧证据：`type UnionTypeNode struct { TypeNodeBase; UnionOrIntersectionTypeNodeBase }`（ast_generated.go:5121-5124）——Go 里这**真的产生了两份 TypeNodeBase**（一份直接、一份在 UnionOrIntersectionTypeNodeBase 里），字段选择靠提升深度；两个 base 都是零尺寸+brand 载体所以无实害。C# 丢弃冗余项后行为等价。

### 3.2 规则 2：mixin 扁平化 = 传递闭包 + 按字段名去重

C# 类无法嵌入 mixin struct，字段必须**生成到引入处**。算法：

```
输入: C# 类 C（具体节点或 Node 系抽象 base），其 extends 列表 E，规则 1 已选出基类 B
1. closure(X) := X 的 extends 传递闭包（含 X 自身各项，全 35 base 图上遍历）
2. newMixins(C) := { M ∈ closure(E) | M 是数据 mixin 或 brand，且 M ∉ closure(B 的声明) }
   （即：只在「首次引入」的类上落字段/接口，父类已引入的不重复）
3. C 的接口声明列表 += newMixins(C) 中的所有 mixin/brand 接口
   （接口自身有继承：FunctionLikeBase : DeclarationBase, LocalsContainerBase 等，C# 接口多继承直接表达）
4. C 的生成字段 := ⋃ { M 的自有字段 | M ∈ newMixins(C) }，**按字段名去重（后到者跳过）**
5. C 的访问器 override := newMixins(C) 对应的 Node 虚访问器（§8 映射表），返回 this
6. 节点自有 members（ast.json definitions[C].members，剔除 inherited/isKindParam/noGo）追加为 C 的直接字段
```

第 4 步的去重就是主计划所说的 **DeclarationBase 菱形**。程序化核验：全 35 base + 192 节点中，唯一经两条路径到达的 mixin 就是 DeclarationBase，共 **13 个位置**（12 个具体节点 + 1 个 Node 系 base）：

| # | 位置 | 路径 1 | 路径 2 |
|---|---|---|---|
| 1 | FunctionDeclaration | extends 直列 DeclarationBase | FunctionLikeWithBodyBase → FunctionLikeBase → DeclarationBase |
| 2 | ClassDeclaration | 直列 | ClassLikeBase → DeclarationBase |
| 3 | CallSignatureDeclaration | 直列 | FunctionLikeBase → DeclarationBase |
| 4 | ConstructSignatureDeclaration | 直列 | FunctionLikeBase → DeclarationBase |
| 5 | ConstructorDeclaration | 直列 | FunctionLikeWithBodyBase → FunctionLikeBase → DeclarationBase |
| 6 | IndexSignatureDeclaration | 直列 | FunctionLikeBase → DeclarationBase |
| 7 | MethodSignatureDeclaration | NamedMemberBase → DeclarationBase | FunctionLikeBase → DeclarationBase |
| 8 | MethodDeclaration | NamedMemberBase → DeclarationBase | FunctionLikeWithBodyBase → FunctionLikeBase → DeclarationBase |
| 9 | ArrowFunction | 直列 | FunctionLikeWithBodyBase → FunctionLikeBase → DeclarationBase |
| 10 | FunctionExpression | 直列 | FunctionLikeWithBodyBase → FunctionLikeBase → DeclarationBase |
| 11 | **AccessorDeclarationBase（base）** | NamedMemberBase → DeclarationBase | FunctionLikeWithBodyBase → FunctionLikeBase → DeclarationBase |
| 12 | GetAccessorDeclaration | （经 11 继承，两路径同 11） | |
| 13 | SetAccessorDeclaration | （经 11 继承，两路径同 11） | |

**Go 侧真相与等价性论证**：Go 在这 13 处物理上存在**两份 `Symbol *Symbol` 字段**（每条路径一份）。选择器 `node.Symbol` 走提升深度最浅那份；`DeclarationData()` 访问器则由手写 override 钉死——`NamedMemberBase.DeclarationData()`（ast.go:1924，返回自己那份）、`FunctionOrConstructorTypeNodeBase.DeclarationData()`（ast.go:2264-2266，返回 FunctionLikeBase 那份；这个手写是因为该 base 里 NodeDefault 的方法与 DeclarationBase 的方法提升深度打平，Go 编译器报歧义）。于是**另一份 Symbol 是永远无人读写的死字段**。已对全仓 grep 验证：不存在任何 `FunctionLikeData().Symbol` / `ClassLikeData().Symbol` / `ClassLikeData().Locals` 之类"经 mixin 访问器摸到死副本"的调用（`ClassLikeData().` 的全部用法只有 HeritageClauses/Members）。因此 C# 去重为单字段**可观察行为完全等价**，且每处省 8 字节。

字段可见性：`private` 字段（ModifiersBase.modifiers、ClassLikeBase.name、NamedMemberBase.name、JSDocCommentBase.text，及 30 个节点级 private `name` member——ast.json 实测共 34 处）在 C# 生成为 `internal`/`private` 字段 + 既有访问器（`Name()`/`Modifiers()`）读取，与 Go 的 package-private 语义对齐（Go 的小写字段包内可见 → C# 用 `internal`，因为 parser/binder 与 ast 在同一程序集 Ts.Compiler 的不同命名空间；05-conventions.md 记此映射）。

### 3.3 规则 3：无字段 brand → 纯标记接口

`newMixins(C)` 中的 brand 项只产出接口声明（无字段、无访问器）。4 个接口见 §2.3。**例外**：TypeSyntaxBase 虽无字段但在 Go 有两个手写方法（ast.go:1616-1618），其行为按 §6.4 再安置，接口本身保持空。

### 3.4（隐含的第四规则）Go 方法提升的显式化：subtree-facts 家族与 mixin 访问器的 override 放置

这是 Go 生成器不需要、C# 生成器**必须补上**的一步，主计划未单列但为三规则的必然推论（不引入新架构决策，只是把 Go 编译器的提升语义写成算法）。

Go 的动态行为由「方法提升：最浅深度胜，平局则编译错（靠手写 override 破局）」决定。涉及两组：

**(a) mixin 访问器**：ast.go 手写在 mixin struct 上（ast.go:1508-1557、1922-1927、2024、2268-2274）：

```go
func (node *DeclarationBase) DeclarationData() *DeclarationBase { return node }        // ast.go:1510
func (node *FunctionLikeBase) LocalsContainerData() *LocalsContainerBase { ... }       // ast.go:1535（钉死自己那份）
func (node *FunctionLikeWithBodyBase) FunctionLikeData() *FunctionLikeBase { ... }     // ast.go:1550
func (node *NamedMemberBase) DeclarationData() *DeclarationBase { ... }                // ast.go:1924（破菱形）
```

C# 中 mixin 是接口、字段在载体类上，因此这些 override 全部落到**引入处的类**上（规则 2 第 5 步），返回 `this`。菱形处因字段已去重，「返回哪份」的问题消失。

**(b) subtree-facts 家族**（4 个虚方法：`SubtreeFacts` / `ComputeSubtreeFacts` / `SubtreeFactsWorker` / `PropagateSubtreeFacts`）。Go 的提供者有四层，按提升深度竞争：

| 提供者 | Go 位置 | 语义 |
|---|---|---|
| NodeDefault | ast.go:1227-1245 | 默认：SubtreeFacts→worker(data)；worker→computeSubtreeFacts()；compute→None；propagate→SubtreeFacts()&^SubtreeExclusionsNode |
| CompositeBase | ast.go:1599-1612 | worker 改为**原子缓存**（facts 位缓存 computeSubtreeFacts 结果）；compute 改为 panic（强制节点自己实现） |
| TypeSyntaxBase | ast.go:1616-1618 | compute/propagate 恒返回 `SubtreeContainsTypeScript` |
| 节点级 | ast.go 手写（如 FunctionDeclaration ast.go:1769）或 generateSubtreeFacts 生成（38 个节点，generate-go-ast.ts:191-217） | 具体事实计算 |

C# 生成算法（模拟提升）：

```
对每个 C# 类 C、方法 m ∈ {ComputeSubtreeFacts, SubtreeFactsWorker, PropagateSubtreeFacts}:
1. 在 Go 嵌入图上求 m 的全部提供者集合 P 与各自深度（节点级手写清单硬编码在生成器配置里，
   同 Go 生成器的 HAND_WRITTEN_BASES 思路，generate-go-ast.ts:222）
2. 取最浅提供者 p：
   - p 为节点级（手写或 generateSubtreeFacts）→ 手写者归手写 partial 文件，生成者进 Visitors.g.cs
   - p 为 CompositeBase → 在引入 CompositeBase 的类上生成缓存 worker（调用共享助手，见下）
   - p 为 TypeSyntaxBase → 在引入处生成 `=> SubtreeFacts.ContainsTypeScript` override；
     若引入处是 TypeNodeBase（抽象类）则只生成一次，全部 TypeNode 派生类自动继承——
     节点级实现天然 override 它，无需特殊处理
   - p 为 NodeDefault → 不生成（Node 上的默认实现兜底）
3. 平局断言：若最浅深度上有 >1 个不同提供者且无节点级破局实现 → 生成器报错
   （Go 侧同款平局会编译失败，故 schema 演进时两边同时被卡住，这是刻意的安全网）
```

CompositeBase 缓存体为避免 72 份重复，生成为对 Node 上共享助手的调用：

```csharp
// Node 上手写（对应 ast.go:1599-1607 的逐字直译）：
protected SubtreeFacts CachedSubtreeFacts(ref uint facts)
{
    var f = (SubtreeFacts)Volatile.Read(ref facts);
    if ((f & SubtreeFacts.SubtreeFactsComputed) == 0)
    {
        f |= ComputeSubtreeFacts() | SubtreeFacts.SubtreeFactsComputed;   // 虚调用 → 最派生实现
        Volatile.Write(ref facts, (uint)f);                               // 幂等，竞态只损时间不损正确性（Go 注释同款）
    }
    return f & ~SubtreeFacts.SubtreeFactsComputed;
}
// 每个 CompositeBase 引入类上生成：
internal override SubtreeFacts SubtreeFactsWorker() => CachedSubtreeFacts(ref facts);
```

注意 Go 的 `subtreeFactsWorker(self nodeData)` 里那个 `self` 参数（ast.go:1231-1237 注释：因 CompositeBase 不继承 Node，需要显式传 self 才能查到正确的 computeSubtreeFacts）在 C# 中**消失**——C# 虚分发天然解决。

---

## 4. Node 抽象类完整成员规格

### 4.1 字段（§1.2 已列）+ 基础成员

| Go（ast.go 行） | C# 签名 | 说明 |
|---|---|---|
| `Kind Kind` | `public SyntaxKind Kind;` | `enum SyntaxKind : short`，351 元素 + `Count` + 34 个 marker 常量（`FirstAssignment = EqualsToken` 等），生成到 SyntaxKind.g.cs |
| `Flags NodeFlags` | `public NodeFlags Flags;` | `[Flags] enum NodeFlags : uint` |
| `Loc core.TextRange` | `public TextRange Loc;` | `struct TextRange { int pos, end }`（Ts.Core），工厂里初始化为 `TextRange.Undefined`（对应 core.UndefinedTextRange()，ast.go:76） |
| `id atomic.Uint64` | `internal ulong id;` | 见 GetNodeId |
| `Parent *Node` | `public Node? Parent;` | |
| `AsNode()` (192) | `public Node AsNode() => this;` | 保 grep 对照，调用点逐字保真 |
| `Pos()` (193) / `End()` (194) | `public int Pos => Loc.Pos;` / `public int End => Loc.End;` | getter→属性（05-conventions.md） |
| `IterChildren()` (195-206) | `public IEnumerable<Node> IterChildren()` | Go iter.Seq 的控制反转在 C# 以**缓冲 List 实现**（ForEachChild 收集后 yield）；正确性等价、丢失早停惰性，剖析发现热点再优化（见 §17 待澄清 6） |
| `KindString()` (226) / `KindValue()` (227) | `public string KindString() => Kind.ToString();` / `public short KindValue() => (short)Kind;` | 枚举 ToString 属 AOT 白名单（07-aot-constraints.md） |
| `Contains(descendant)` (1155-1167) | `public bool Contains(Node? descendant)` | 手写直译（Parent 链上溯 + SourceFile 例外 panic→InvalidOperationException） |
| `JSDoc(file)` (1560) / `EagerJSDoc(file)` (1578) | `public Node[] JSDoc(SourceFile? file)` 等 | 手写直译；依赖 §12 的 jsdocCache/锁 |
| `Symbol()` (240) / `LocalSymbol()` (248) / `Locals()` (256) / `Body()` (264) | `public Symbol? Symbol { get { var d = DeclarationData(); return d?.Symbol; } }` 等 4 个 | nil-data 容忍便捷访问器（不是 nil 接收者），`?.` 直译 Go 的 `if data != nil` |
| `Decorators()` (228-233) | `public Node[]? Decorators()` | 手写直译（Modifiers()==null→null；否则 Filter(IsDecorator)） |
| `ParameterList()` (216) / `Parameters()` (217) | `public ParameterList ParameterList() => FunctionLikeData()!.Parameters;` 等 | 注意 Go 版**不做 nil 检查**（data 为 nil 时 panic）→ C# 用 `!`，NRE 对应 panic |
| `ModifierFlags()` (612) / `ModifierNodes()` (620) | 手写直译 | |

`GetNodeId`（utilities.go:22-31，全局计数器 ids.go）：

```csharp
// Ast/Ids.cs（手写）
private static ulong nextNodeId;
public static NodeId GetNodeId(Node node)
{
    var id = Volatile.Read(ref node.id);
    if (id == 0)
    {
        id = Interlocked.Increment(ref nextNodeId);          // 最坏情况 CAS 失败烧掉几个 id，与 Go 注释同款
        var seen = Interlocked.CompareExchange(ref node.id, id, 0);
        if (seen != 0) id = seen;
    }
    return new NodeId(id);
}
```

`NodeId`/`SymbolId`（ids.go:3-6 的定义类型）→ `readonly record struct NodeId(ulong Value)`（主计划 §一.6 结构键惯用法）。

### 4.2 虚方法表：对照 Go `nodeData` 21 方法（ast.go:1181-1202）

| # | Go nodeData 方法 | NodeDefault 默认（ast.go 行） | C# 签名（Node 上） | C# 默认实现 |
|---|---|---|---|---|
| 1 | `AsNode() *Node` | 1210 | *(非虚)* `public Node AsNode() => this;` | — 接口方法退化 |
| 2 | `ForEachChild(v Visitor) bool` | 1211 `return false` | `public virtual bool ForEachChild(Visitor v)` | `=> false;` |
| 3 | `VisitEachChild(v *NodeVisitor) *Node` | 1213 `return node.AsNode()` | `public virtual Node VisitEachChild(NodeVisitor v)` | `=> this;` |
| 4 | `Clone(v NodeFactoryCoercible) *Node` | 1214 `return nil` | `public virtual Node? Clone(NodeFactoryCoercible f)` | `=> null;` |
| 5 | `Name() *DeclarationName` | 1215 `return nil` | `public virtual Node? Name()` | `=> null;` |
| 6 | `Modifiers() *ModifierList` | 1216 `return nil` | `public virtual ModifierList? Modifiers()` | `=> null;` |
| 7 | `setModifiers(*ModifierList)` | 1217 空体 | `internal virtual void SetModifiers(ModifierList? modifiers)` | `{ }` no-op |
| 8 | `FlowNodeData() *FlowNodeBase` | 1218 | `public virtual FlowNodeBase? FlowNodeData()` | `=> null;` |
| 9 | `DeclarationData() *DeclarationBase` | 1219 | `public virtual DeclarationBase? DeclarationData()` | `=> null;` |
| 10 | `ExportableData() *ExportableBase` | 1220 | `public virtual ExportableBase? ExportableData()` | `=> null;` |
| 11 | `LocalsContainerData() *LocalsContainerBase` | 1221 | `public virtual LocalsContainerBase? LocalsContainerData()` | `=> null;` |
| 12 | `FunctionLikeData() *FunctionLikeBase` | 1222 | `public virtual FunctionLikeBase? FunctionLikeData()` | `=> null;` |
| 13 | `ClassLikeData() *ClassLikeBase` | 1223 | `public virtual ClassLikeBase? ClassLikeData()` | `=> null;` |
| 14 | `BodyData() *BodyBase` | 1224 | `public virtual BodyBase? BodyData()` | `=> null;` |
| 15 | `LiteralLikeData() *LiteralLikeNodeBase` | 1225 | `public virtual LiteralLikeNodeBase? LiteralLikeData()` | `=> null;` |
| 16 | `TemplateLiteralLikeData() *TemplateLiteralLikeNodeBase` | 1226 | `public virtual TemplateLiteralLikeNodeBase? TemplateLiteralLikeData()` | `=> null;` |
| 17 | `SubtreeFacts() SubtreeFacts` | 1227-1229 | `public virtual SubtreeFacts SubtreeFacts()` | `=> SubtreeFactsWorker();` |
| 18 | `computeSubtreeFacts() SubtreeFacts` | 1239-1241 | `internal virtual SubtreeFacts ComputeSubtreeFacts()` | `=> SubtreeFacts.None;` |
| 19 | `subtreeFactsWorker(self nodeData) SubtreeFacts` | 1231-1237 | `internal virtual SubtreeFacts SubtreeFactsWorker()` | `=> ComputeSubtreeFacts();`（self 参数消失，§3.4） |
| 20 | `propagateSubtreeFacts() SubtreeFacts` | 1243-1245 | `internal virtual SubtreeFacts PropagateSubtreeFacts()` | `=> SubtreeFacts() & ~SubtreeFacts.SubtreeExclusionsNode;` |
| — | *(Node 上转发方法 ast.go:207-225)* | — | 不需要：C# 虚方法即入口，Go 的 `n.data.X()` 转发层整个消失 | |

**ForEachChild 分发的取舍**：Go 额外生成了 `(n *Node) ForEachChild` 的 Kind 跳表分发（generate-go-ast.ts:493-527），注释言明动机是**逃逸分析**——接口调用会迫使访问者闭包上堆。这是 Go 专属优化：C# 没有逃逸分析问题（delegate 本来就是堆对象），单次虚调用即等价语义，故 C# 直接用虚方法 override，不生成跳表。行为（无孩子节点返回 false）由默认实现兜底，逐字节输出无影响。

`Visitor` 委托：`public delegate bool Visitor(Node node);`（ast.go:28）。包级助手（ast.go:30-58，参数 nil 容忍）：

```csharp
internal static bool Visit(Visitor v, Node? node) => node != null && v(node);
internal static bool VisitNodes(Visitor v, Node[]? nodes) { ... }        // Go []*Node 的 for range
internal static bool VisitNodeList(Visitor v, NodeList? list) => list != null && VisitNodes(v, list.Nodes);
internal static bool VisitModifiers(Visitor v, ModifierList? modifiers) => modifiers != null && VisitNodes(v, modifiers.Nodes);
```

### 4.3 As* 转换：192 生成 + 2 手写

生成规则（对应 generate-go-ast.ts:263-271）：

```csharp
public Identifier AsIdentifier() => (Identifier)this;      // Go: n.data.(*Identifier)，失败 panic → InvalidCastException
```

- 每个节点定义 1 个，共 **192**（主计划的 "~190" 为约数，实测 192；见 §17 待澄清 1）。
- 手写 2 个：`AsFlowSwitchClauseData` / `AsFlowReduceLabelData`（ast.go:1171-1177）——这两个 data 是 flow 图借用 Node 容器的特例，C# 中同样手写 `(FlowSwitchClauseData)this`（这两个类型定义在 flow.go，随 Binder 阶段移植）。
- 多 Kind 类（Token、KeywordExpression、KeywordTypeNode、ForInOrOfStatement、CaseOrDefaultClause、BindingPattern、JSDocParameterOrPropertyTag、TypeAliasDeclaration、ImportDeclaration）不产生额外 As*：转换按**类**不按 Kind（`AsTypeAliasDeclaration()` 对 JSTypeAliasDeclaration 的节点同样成功——两 Kind 共享一个类，与 Go 一致）。

### 4.4 switch 型多态访问器：**手写，不生成**

ast.go:188-190 的注释言明这批访问器刻意用 Kind switch 而非 nodeData 方法实现（权衡代码尺寸）。它们**在 Go 就是手写**，C# 保持手写、逐 case 直译，放 `Ast/Node.Accessors.cs`（手写 partial）。全量清单（ast.go 行号）：

| 访问器 | 行 | case 数 | 失败行为 | C# 返回类型 |
|---|---|---|---|---|
| `Text()` | 272-308 | 16 | panic → `InvalidOperationException` | `string` |
| `Expression()` | 310-384 | 35 | panic | `Node` |
| `RawText()` | 386-396 | 3 | panic | `string` |
| `ArgumentList()` / `Arguments()` | 476-492 | 2 | panic / — | `NodeList` / `Node[]` |
| `TypeArgumentList()` / `TypeArguments()` | 494-524 | 9 | panic / — | `NodeList?`（Go 返回可空列表） |
| `TypeParameterList()` / `TypeParameters()` | 526-553 | 5+FunctionLikeData 兜底 | panic | 同上 |
| `MemberList()` / `Members()` | 555-579 | 6 | panic | `NodeList` / `Node[]` |
| `StatementList()` / `Statements()` | 581-601 | 4 | panic | 同上 |
| `CanHaveStatements()` | 603-610 | 5 | — false | `bool` |
| `Type()` | 628-686 | 27+FunctionLikeData 兜底 | **返回 nil**（非 panic！） | `Node?` |
| `Initializer()` | 750-774 | 10 | panic | `Node?` |
| `TagName()` | 804-856 | 24 | panic | `Node` |
| `PropertyName()` / `PropertyNameOrName()` | 858-876 | 3 | — nil | `Node?` |
| `IsTypeOnly()` | 878-892 | 5 | — false | `bool` |
| `CommentList()` / `Comments()` | 895-951 | 22 | panic | `NodeList?` |
| `Label()` | 953-963 | 3 | panic | `Node` |
| `Attributes()` | 965-973 | 2 | panic | `Node` |
| `Children()` | 975-983 | 2 | panic | `NodeList` |
| `ModuleSpecifier()` | 985-995 | 3 | panic | `Node` |
| `ImportClause()` | 997-1005 | 2 | panic | `Node?` |
| `Statement()` | 1007-1023 | 6 | panic | `Node` |
| `PropertyList()` / `Properties()` | 1025-1041 | 2 | panic | `NodeList` / `Node[]` |
| `ElementList()` / `Elements()` | 1043-1065 | 5 | panic | 同上 |
| `PostfixToken()` | 1067-1089 | 9 | — nil | `Node?` |
| `QuestionToken()` | 1091-1107 | 4+PostfixToken 兜底 | — nil | `Node?` |
| `QuestionDotToken()` | 1109-1121 | 4 | panic | `Node?` |
| `TypeExpression()` | 1123-1141 | 7 | panic | `Node` |
| `ClassName()` | 1143-1151 | 2 | panic | `Node` |

直译示例（Text，ast.go:272-308）：

```csharp
public string Text() => Kind switch
{
    SyntaxKind.Identifier => AsIdentifier().Text,
    SyntaxKind.PrivateIdentifier => AsPrivateIdentifier().Text,
    SyntaxKind.StringLiteral => AsStringLiteral().Text,
    // …16 个 case 逐一对应…
    SyntaxKind.JsxNamespacedName => AsJsxNamespacedName().Namespace.Text() + ":" + AsJsxNamespacedName().name.Text(),
    SyntaxKind.JSDocText => string.Concat(AsJSDocText().text),   // Go strings.Join(text, "")
    _ => throw new InvalidOperationException($"Unhandled case in Node.Text: {Kind}"),
};
```

> `Text()` 保持**方法**而非属性：它有失败分支且与 mixin 字段 `LiteralLikeNodeBase.Text`（属性）同名不同物，方法/字段区分与 Go 完全一致（Go 里 `node.Text()` 是 Node 的方法、`node.AsIdentifier().Text` 是字段）。

### 4.5 MutableNode

Go 用 `type MutableNode Node` + `AsMutable()`（ast.go:235-238）做**编译期写权限标记**，运行时是同一对象。C# 无零开销的同构手段，映射：

- `SetExpression`（ast.go:398-474）、`SetType`（688-748）、`SetInitializer`（776-802）、`SetModifiers`（238）→ Node 上的 `internal` 方法（写权限用可见性表达：这些 setter 只被 parser/transformer 使用，同程序集）。
- `AsMutable()` → `public Node AsMutable() => this;`（保调用点形态）——**已裁决（R9）：保留同名一行方法以保调用点形态，不删调用点**，见 05-conventions.md 对应条目。

---

## 5. （并入 §3，见生成器三规则）

*本节号保留占位，避免后续文档引用错位；内容已在 §3 完整给出。*

---

## 6. （并入 §3.4，Go 方法提升显式化）

*同上，内容见 §3.4。*

---

## 7. （并入 §4，Node 成员规格）

*同上，内容见 §4。*

---

## 8. mixin 虚访问器机制：完整示例与全量映射

### 8.1 接口定义（生成到 Nodes.g.cs）

mixin 接口成员 = mixin 自有字段的属性化（goOnly 字段一样进接口——它们是 Go 侧数据，C# 全保留）：

```csharp
public interface DeclarationBase { Symbol? Symbol { get; set; } }
public interface ExportableBase { Symbol? LocalSymbol { get; set; } }
public interface LocalsContainerBase { SymbolTable? Locals { get; set; } Node? NextContainer { get; set; } }
public interface FlowNodeBase { FlowNode? FlowNode { get; set; } }
public interface LiteralLikeNodeBase { string Text { get; set; } TokenFlags TokenFlags { get; set; } }
public interface TemplateLiteralLikeNodeBase : LiteralLikeNodeBase { string RawText { get; set; } TokenFlags TemplateFlags { get; set; } }
public interface FunctionLikeBase : DeclarationBase, LocalsContainerBase
{
    NodeList? TypeParameters { get; set; }   // *TypeParameterList → NodeList（别名，§13）
    NodeList Parameters { get; set; }
    Node? Type { get; set; }
    Node? FullSignature { get; set; }
}
public interface BodyBase { Node? AsteriskToken { get; set; } Node? Body { get; set; } FlowNode? EndFlowNode { get; set; } }
public interface FunctionLikeWithBodyBase : FunctionLikeBase, BodyBase { }
public interface ClassLikeBase : DeclarationBase, ExportableBase, LocalsContainerBase
{
    NodeList? TypeParameters { get; set; }
    NodeList? HeritageClauses { get; set; }
    NodeList Members { get; set; }
    // 注：private 字段 name、modifiers 与 CompositeBase.facts 不进接口（Go 侧也不经 *ClassLikeBase 公有访问），
    //     经 Name()/Modifiers() 虚访问器暴露
}
public interface NamedMemberBase : DeclarationBase { Node? PostfixToken { get; set; } }  // name/modifiers 同上不进接口
// ModifiersBase / CompositeBase：全部字段私有，接口为空标记（modifiers 经 Modifiers()/SetModifiers() 虚访问器；facts 经 CachedSubtreeFacts）
public interface ModifiersBase { }
public interface CompositeBase { }
```

> 私有字段（modifiers/name/facts/text）**不入接口**：Go 侧这些字段小写，任何 `*ModifiersBase` 持有者也只能在 ast 包内摸到，跨包一律走 `Modifiers()`/`Name()` 方法——接口收公有字段、私有走虚访问器，恰好 1:1。

### 8.2 Node 虚访问器 ↔ mixin ↔ override 放置的全量映射

「引入类」= 规则 2 第 2 步算出的首次引入位置（含抽象 base；其派生类自动继承 override）：

| Node 虚访问器 | 携带 mixin | override 引入类（全量） |
|---|---|---|
| `FlowNodeData()` | FlowNodeBase | **StatementBase**（全部语句）、**AccessorDeclarationBase**、Identifier、BindingElement、MethodDeclaration、PropertyDeclaration、ArrowFunction、FunctionExpression、QualifiedName、ElementAccessExpression、PropertyAccessExpression、BinaryExpression、JsxElement、JsxSelfClosingElement、JsxFragment 等（44 载体经 15 个引入类覆盖） |
| `DeclarationData()` | DeclarationBase | 直列引入的 38 个具体节点 + 经 FunctionLikeBase/ClassLikeBase/NamedMemberBase 携带的引入类（如 FunctionTypeNode 经 FunctionOrConstructorTypeNodeBase）——54 载体 |
| `ExportableData()` | ExportableBase | FunctionDeclaration、ClassDeclaration/ClassExpression（经 ClassLikeBase）、InterfaceDeclaration、TypeAliasDeclaration、EnumDeclaration、ModuleDeclaration、ImportEqualsDeclaration 等 14 载体 |
| `Modifiers()` / `SetModifiers()` | ModifiersBase | 33 载体的引入类；**特例** BinaryExpression 的 setModifiers 手写（ast.go:2056，它有 modifiers member 但走特殊路径）→ 手写 partial |
| `LocalsContainerData()` | LocalsContainerBase | Block、CaseBlock、CatchClause、ForStatement、ForInOrOfStatement、SourceFile、ModuleDeclaration、TypeAliasDeclaration、MappedTypeNode、ConditionalTypeNode + 经 FunctionLikeBase 的 14 个函数样载体 = 27 载体 |
| `FunctionLikeData()` | FunctionLikeBase | 14 载体：4 个签名声明节点、FunctionTypeNode/ConstructorTypeNode（经 FunctionOrConstructorTypeNodeBase）、JSDocSignature(若含)、FunctionDeclaration/MethodDeclaration/ConstructorDeclaration/Get/SetAccessor/ArrowFunction/FunctionExpression（经 FunctionLikeWithBodyBase） |
| `BodyData()` | BodyBase | FunctionDeclaration、MethodDeclaration、ConstructorDeclaration、AccessorDeclarationBase、ArrowFunction、FunctionExpression、ClassStaticBlockDeclaration 等 8 载体 |
| `ClassLikeData()` | ClassLikeBase | ClassDeclaration、ClassExpression（2 载体） |
| `LiteralLikeData()` | LiteralLikeNodeBase | **LiteralExpressionBase**（StringLiteral/NumericLiteral/BigIntLiteral/RegularExpressionLiteral/NoSubstitutionTemplateLiteral）、**TemplateLiteralLikeNodeBase 载体**（TemplateHead/Middle/Tail）、JsxText——9 载体 |
| `TemplateLiteralLikeData()` | TemplateLiteralLikeNodeBase | NoSubstitutionTemplateLiteral、TemplateHead、TemplateMiddle、TemplateTail（4 载体） |
| `Name()` | （非 mixin 专属） | ClassLikeBase 引入类、NamedMemberBase 引入类 + 30 个 private `name` member 的节点（generateNameAccessor 对应物，generate-go-ast.ts:747-765） |
| —（无访问器） | CompositeBase | 72 载体：字段 `private uint facts` + `SubtreeFactsWorker()` 缓存 override（§3.4） |

### 8.3 端到端示例：MethodDeclaration

ast.json 定义：`extends [NodeBase, NamedMemberBase, FunctionLikeWithBodyBase, FlowNodeBase, ClassElementBase, ObjectLiteralElementBase, CompositeBase]`，members：AsteriskToken(inherited)、TypeParameters(inherited)、Parameters(inherited)、Type(inherited)、FullSignature(inherited)、Body(inherited, FunctionBody 窄化)。Go struct（ast_generated.go:3303-3311）就是七个嵌入。C# 生成：

```csharp
public sealed partial class MethodDeclaration : Node,
    NamedMemberBase, FunctionLikeWithBodyBase, FlowNodeBase, ClassElementBase, ObjectLiteralElementBase, CompositeBase
{
    // —— 规则 2 扁平化（按引入序；DeclarationBase.Symbol 经 NamedMemberBase 与 FunctionLikeWithBodyBase 双路径到达 → 去重只留一份）
    public Symbol? Symbol { get; set; }                    // DeclarationBase
    internal ModifierList? modifiers;                      // ModifiersBase (private)
    internal Node? name;                                   // NamedMemberBase (private, *PropertyName)
    public Node? PostfixToken { get; set; }                // NamedMemberBase
    public SymbolTable? Locals { get; set; }               // LocalsContainerBase（经 FunctionLikeBase）
    public Node? NextContainer { get; set; }               // LocalsContainerBase
    public NodeList? TypeParameters { get; set; }          // FunctionLikeBase
    public NodeList Parameters { get; set; } = null!;      // FunctionLikeBase（必填，工厂赋值）
    public Node? Type { get; set; }                        // FunctionLikeBase
    public Node? FullSignature { get; set; }               // FunctionLikeBase
    public Node? AsteriskToken { get; set; }               // BodyBase
    public Node? Body { get; set; }                        // BodyBase
    public FlowNode? EndFlowNode { get; set; }             // BodyBase
    public FlowNode? FlowNode { get; set; }                // FlowNodeBase
    private uint facts;                                    // CompositeBase

    // —— 规则 2 第 5 步：mixin 虚访问器 override（对应 Go 提升/手写 override 的净效果）
    public override DeclarationBase? DeclarationData() => this;          // Go: NamedMemberBase override, ast.go:1924
    public override ModifierList? Modifiers() => modifiers;             // ast.go:1925
    internal override void SetModifiers(ModifierList? m) => modifiers = m;
    public override Node? Name() => name;                               // ast.go:1927
    public override LocalsContainerBase? LocalsContainerData() => this; // Go: FLWBB override, ast.go:1546
    public override FunctionLikeBase? FunctionLikeData() => this;       // ast.go:1550
    public override BodyBase? BodyData() => this;                       // ast.go:1553
    public override FlowNodeBase? FlowNodeData() => this;               // ast.go:1557
    internal override SubtreeFacts SubtreeFactsWorker() => CachedSubtreeFacts(ref facts);   // CompositeBase
    // ComputeSubtreeFacts / PropagateSubtreeFacts：Go 手写（ast.go:1967-1990）→ 手写 partial 文件 Ast/Ast.cs
}
```

调用点保真效果：`node.DeclarationData().Symbol = sym`、`node.FunctionLikeData().FullSignature`、`node.BodyData().EndFlowNode`、`flowNodeData != nil → != null` 全部逐字成立（§9 对照表）。

---

## 9. 真实调用点移植前后对照表（≥20，全部摘自 checker/binder/checker-jsx 实源）

命名空间前缀约定：Go 的 `ast.Xxx` 包级函数经 `using static Ts.Ast.NodeTests;`（Is* 守卫）与 `Ts.Ast.AstUtilities`（工具函数）消化，调用点形态尽量与 Go 包内形态一致；`ast.KindXxx` → `SyntaxKind.Xxx`。最终静态类命名归 05-conventions.md。

| # | Go 原文（file:line） | C# 移植文 |
|---|---|---|
| 1 | `commonEntityName.AsIdentifier().Text != individualEntityName.AsIdentifier().Text`（checker.go:28551） | `commonEntityName.AsIdentifier().Text != individualEntityName.AsIdentifier().Text`（字符串 `!=` 语义相同） |
| 2 | `node.DeclarationData().Symbol = symbol`（binder.go:2522） | `node.DeclarationData()!.Symbol = symbol;` |
| 3 | `if flowNodeData := node.FlowNodeData(); flowNodeData != nil { flowNodeData.FlowNode = nil }`（binder.go:1652-1654） | `if (node.FlowNodeData() is { } flowNodeData) flowNodeData.FlowNode = null;` |
| 4 | `data := node.FlowNodeData(); if data != nil { data.FlowNode = flowNode }`（binder.go:2489-2493 setFlowNode） | `var data = node.FlowNodeData(); if (data != null) data.FlowNode = flowNode;` |
| 5 | `node.ForEachChild(c.checkSourceElement)`（checker.go:2292） | `node.ForEachChild(checkSourceElement);`（方法组转 Visitor 委托） |
| 6 | `return node.ForEachChild(visit)`（checker.go:2888，递归局部闭包） | `return node.ForEachChild(visit);`（`Visitor visit = null!; visit = n => {...}` 局部递归委托模式，05-conventions.md） |
| 7 | `if ast.IsClassLike(node) && !inAmbientContext {`（checker.go:3603） | `if (IsClassLike(node) && !inAmbientContext) {` |
| 8 | `typeParameterDeclarations[j].Symbol() == node.Symbol()`（checker.go:6991） | `typeParameterDeclarations[j].Symbol == node.Symbol`（类默认引用相等 = Go 指针比较） |
| 9 | `if localsContainer := container.LocalsContainerData(); localsContainer != nil { if local := localsContainer.Locals[name]; local != nil {`（binder.go:1293-1295） | `if (container.LocalsContainerData() is { } localsContainer) { if (localsContainer.Locals?.TryGetValue(name, out var local) == true) {`（map 取值→TryGetValue；SymbolTable 设计随 binder 档） |
| 10 | `b.lastContainer.LocalsContainerData().NextContainer = next`（binder.go:2515） | `lastContainer.LocalsContainerData()!.NextContainer = next;` |
| 11 | `condExpr.AsBinaryExpression().OperatorToken.Kind == ast.KindBarBarToken`（checker.go:3810） | `condExpr.AsBinaryExpression().OperatorToken.Kind == SyntaxKind.BarBarToken` |
| 12 | `nameNode := node.Name()`（binder.go:363） | `var nameNode = node.Name();` |
| 13 | `ast.IsIdentifier(node) && node.Text() != "" && node.Text()[0] == '_'`（checker.go:7245） | `IsIdentifier(node) && node.Text() != "" && node.Text()[0] == '_'`（C# 字符串索引器给 char，`'_'` 比较同义） |
| 14 | `ast.IsForInOrOfStatement(grandparent) && isSameScopeDescendentOf(usage, grandparent.Expression(), declContainer)`（checker.go:2069） | `IsForInOrOfStatement(grandparent) && IsSameScopeDescendentOf(usage, grandparent.Expression(), declContainer)` |
| 15 | `endFlowNode := fn.BodyData().EndFlowNode`（checker.go:20213） | `var endFlowNode = fn.BodyData()!.EndFlowNode;`（Go 此处无 nil 检查，NRE 对应 panic） |
| 16 | `bodyData := node.BodyData(); if bodyData != nil && ast.NodeIsPresent(bodyData.Body) {`（binder.go:1555-1556） | `var bodyData = node.BodyData(); if (bodyData != null && NodeIsPresent(bodyData.Body)) {` |
| 17 | `if node.FunctionLikeData().FullSignature != nil {`（checker.go:3427） | `if (node.FunctionLikeData()!.FullSignature != null) {` |
| 18 | `getterFlags := getter.ModifierFlags()`（checker.go:2950） | `var getterFlags = getter.ModifierFlags();` |
| 19 | `declaration.Initializer() == nil && !isExclamationToken(declaration.PostfixToken())`（checker.go:1920） | `declaration.Initializer() == null && !IsExclamationToken(declaration.PostfixToken())` |
| 20 | `immediateDeclaration.AsVariableDeclaration().ExclamationToken == nil`（checker.go:11120） | `immediateDeclaration.AsVariableDeclaration().ExclamationToken == null` |
| 21 | `node.AsPropertyAccessExpression().Name()`（checker.go:11221） | `node.AsPropertyAccessExpression().Name()`（PropertyAccessExpression 的 private name + Name() 生成访问器） |
| 22 | `ast.IsSourceFile(d) && d.AsSourceFile().GlobalExports != nil`（checker.go:1830） | `IsSourceFile(d) && d.AsSourceFile().GlobalExports != null` |
| 23 | `GetSymbolNameForPrivateIdentifier(containingClass.Symbol(), name.Text())`（binder.go:326） | `GetSymbolNameForPrivateIdentifier(containingClass.Symbol!, name.Text())` |
| 24 | `tagName := node.TagName()`（checker/jsx.go:141） | `var tagName = node.TagName();` |
| 25 | `key := InstantiationExpressionKey{nodeId: ast.GetNodeId(node), typeId: exprType.id}`（checker.go:10639） | `var key = new InstantiationExpressionKey(GetNodeId(node), exprType.Id);`（readonly record struct 键，03-checker-design.md） |
| 26 | `errorNode.Loc = core.NewTextRange(args[argCount].Pos(), args[len(args)-1].End())`（checker.go:9296） | `errorNode.Loc = new TextRange(args[argCount].Pos, args[^1].End);` |
| 27 | `reference := c.factory.NewPropertyAccessExpression(c.factory.NewKeywordExpression(ast.KindThisKeyword), nil, propName, ast.NodeFlagsNone)`（checker.go:4947） | `var reference = factory.NewPropertyAccessExpression(factory.NewKeywordExpression(SyntaxKind.ThisKeyword), null, propName, NodeFlags.None);` |
| 28 | `span := scanner.GetRangeOfTokenAtPosition(b.file, node.Pos())`（binder.go:2711） | `var span = Scanner.GetRangeOfTokenAtPosition(file, node.Pos);` |

> 规律总结（供 05-conventions.md 收录）：① `X()` 数据访问器 + `!= nil` 判空的组合逐字翻译为 `X()` + `!= null` 或模式匹配；② Go 无判空直接链式访问 → C# `!`（保留 panic 语义）；③ 指针相等 → 引用相等 `==`；④ `ast.KindXxx` → `SyntaxKind.Xxx`；⑤ 字段访问（`.Text`/`.OperatorToken`）不变。

---

## 10. NodeFactory API 保真表

### 10.1 结构

```csharp
public sealed class NodeFactory : NodeFactoryCoercible
{
    private readonly NodeFactoryHooks hooks;      // struct/class，三个可空委托
    private int nodeCount;
    private int textCount;
    public NodeFactory(NodeFactoryHooks hooks) => this.hooks = hooks;   // ast.go:70-72
    public int NodeCount() => nodeCount;          // ast.go:90
    public int TextCount() => textCount;          // ast.go:94
    public NodeFactory AsNodeFactory() => this;   // ast.go:98
    private Node NewNode(SyntaxKind kind, Node n) { nodeCount++; return InitNode(kind, n, hooks); }
}
public struct NodeFactoryHooks     // ast.go:60-64
{
    public Action<Node>? OnCreate;
    public Action<Node, Node>? OnUpdate;   // (updated, original)
    public Action<Node, Node>? OnClone;
}
public interface NodeFactoryCoercible { NodeFactory AsNodeFactory(); }   // ast.go:66-68；I 前缀问题见 §17-5
```

**Arena 全删**（主计划决策）：Go 侧 48 个 `core.Arena[T]` 字段（ast_generated.go:20-73；46 节点 + modifierList + nodeList）不生成；`f.xxxArena.New()` → `new T()`。`ReleaseArenas()`（ast.go:3053-3059）随之变成无意义 API → 删除并清理调用点（见 §17-7）。

自由函数（ast.go:74-119）逐一保留为 NodeFactory 静态/私有方法：

- `newNode(kind, data, hooks)` → `InitNode`：`n.Loc = TextRange.Undefined; n.Kind = kind;` + `hooks.OnCreate?.Invoke(n)`。Go 的 `n.data = data` 一句**消失**（无 data 指针）。
- `updateNode(updated, original, hooks)`（ast.go:102-111）：`updated != original` 时复制 `Flags`/`Loc` + OnUpdate 钩子——**逐字直译**（引用比较）。
- `cloneNode`（ast.go:113-119）：调 updateNode 后触发 OnClone。

### 10.2 生成面（保真计数）

| 家族 | 数量 | 生成规则出处（generate-go-ast.ts） | C# 形态 |
|---|---|---|---|
| `NewXxx` | **193** = 191 个单 Kind 生成 + 2 个 kindAlias 变体（NewJSTypeAliasDeclaration、NewJSImportDeclaration；SourceFile handWritten 不生成） | :280-346 | `public Node NewFunctionDeclaration(ModifierList? modifiers, Node? asteriskToken, ...) { var data = new FunctionDeclaration(); data.modifiers = modifiers; ...; return NewNode(SyntaxKind.FunctionDeclaration, data); }` 参数为 Go 参数序原样；`bitmask` member（如 Flags 参数）按 :303-326 的 `node.Flags \|= param & mask` 逻辑直译；含 string member 的节点 `textCount++`（:309-311） |
| `UpdateXxx` | **166**（叶节点与无孩子节点不生成，:355-359） | :350-421 | 全成员 `!=` 比较（raw slice 用 `!core.Same` → `ReferenceEquals` 数组引用比较）；不同则 `updateNode(NewXxx(...), node, hooks)`；多 Kind 节点按 node.Kind switch 分派到对应 NewXxx（:391-410） |
| `Clone` | **191**（+SourceFile 手写 ast.go:2680-2685） | :598-647 | `public override Node Clone(NodeFactoryCoercible f) => CloneNode(f.AsNodeFactory().NewXxx(...), this, f.AsNodeFactory().hooks);` inherited ModifierLike member 取 `node.Modifiers()`（:613-615） |
| `ForEachChild` | **167**（有孩子者；SourceFile 手写 ast.go:2647） | :425-479 | `visit/visitNodeList/visitModifiers/visitNodes` 链 `\|\|` 短路，直译 |
| `VisitEachChild` | **166** | :531-594 | `v.Factory.UpdateXxx(node, v.VisitModifiers(...), v.VisitNode(...), ...)`；raw list 先 `core.SameMap` 局部变量（→ `SameMap` 助手，Ts.Core）；`visit` 标记成员走专用钩子（`v.visitFunctionBody` 等） |
| `IsXxx` | **232** | :651-696 | `public static bool IsIdentifier(Node node) => node.Kind == SyntaxKind.Identifier;` 多 Kind 节点按 Kind 逐个生成（IsForInStatement/IsForOfStatement…）；类型参数节点生成 switch（IsToken 型） |
| kind 别名守卫 | 34−1 | :706-743 | `IsTokenKind`（range：`kind >= SyntaxKind.FirstToken && kind <= SyntaxKind.LastToken`）/枚举 switch；跳过与 utilities.go 手写 `IsJSDocKind` 冲突者（:709 注释） |
| 手写工厂 | 5 | — | `NewNodeList`（ast.go:128）、`NewModifierList`（159）、`NewModifier`（1671，转发 NewToken）、`NewSourceFile`（2546，含 fileName 规范化 panic → 异常）、`NewCommentRange`（2985）；另有 `UpdateSourceFile`（2691）与 `UpdateJSDocParameterOrPropertyTag` 对应的手写 visitor 桥（ast.go:3040-3051） |

`handWrittenVisitor`（JSDocParameterOrPropertyTag）：生成的 ForEachChild/VisitEachChild 转发到手写 `forEachChild_JSDocParameterOrPropertyTag`（ast.go:3040-3047，IsNameFirst 决定孩子顺序）→ C# 同样转发到手写 partial 方法。

### 10.3 NodeVisitor（visitor.go，手写移植，非生成）

`NodeVisitor{ Visit func; Factory; Hooks }`（visitor.go:9-13）→ `public sealed class NodeVisitor { public Func<Node, Node> Visit; public NodeFactory Factory; public NodeVisitorHooks Hooks; }`；公有 `VisitSourceFile/VisitNode/VisitEmbeddedStatement/VisitNodes/VisitModifiers/VisitSlice/VisitEachChild` 与内部 `visitNode/visitToken/visitNodes/visitModifiers/visitParameters/visitTopLevelStatements/visitFunctionBody/visitIterationBody/liftToBlock` 逐一直译。生成代码只调用其中 8 个内部方法（generate-go-ast.ts:577-586 的映射）。

---

## 11. NodeList / ModifierList 设计

Go（ast.go:121-173）：

```csharp
public class NodeList                          // 非 sealed：ModifierList 继承之（Go 用嵌入）
{
    public TextRange Loc;
    public Node[] Nodes;                       // Go []*Node → Node[]（05-conventions.md 切片规约；nil 切片 → 空数组或 null 的逐案表同档）
    public NodeList(Node[] nodes) { Loc = TextRange.Undefined; Nodes = nodes; }
    public int Pos => Loc.Pos;
    public int End => Loc.End;
    public bool HasTrailingComma()             // ast.go:138-144
        => Nodes.Length != 0 && Nodes[^1].End < End;
    public NodeList Clone(NodeFactoryCoercible f)   // ast.go:146-150：新建 + 复制 Loc
    { var r = f.AsNodeFactory().NewNodeList(Nodes); r.Loc = Loc; return r; }
}

public sealed class ModifierList : NodeList    // Go: 嵌入 NodeList + ModifierFlags（ast.go:154-157）
{
    public ModifierFlags ModifierFlags;
    // NewModifierList 工厂里计算 ModifiersToFlags(nodes)（ast.go:159-165）
    public new ModifierList Clone(NodeFactory f) { ... }   // Go 独立签名（ast.go:167-173），非虚 shadow，与 Go 一致
}
```

要点：

- `NodeList` 是**类不是 struct**：Go 中到处传 `*NodeList` 并以指针身份参与 `Update*` 的 `!=` 比较（引用同一性承重——`UpdateXxx` 靠它判断"孩子没变"）。struct 化会破坏该语义。
- 23 个 list 别名（`ParameterList`、`TypeParameterList`…）→ `global using ParameterList = Ts.Ast.NodeList;`（§13），注释保留元素类型（Go 同款注释，ast_generated.go 的 `// NodeList[*ParameterDeclaration]`）。
- `ModifierList.Clone` 在 Go 接收 `*NodeFactory` 而 `NodeList.Clone` 接收接口——保持两个签名原样（shadow，不虚化），避免调用点漂移。

---

## 12. SourceFile 专节

SourceFile 是唯一 handWritten 节点（ast.json `"handWritten": true`），Go 定义在 ast.go:2464-2544，C# 全手写为 `public sealed partial class SourceFile : Node, DeclarationBase, LocalsContainerBase, CompositeBase`（extends：NodeBase, DeclarationBase, LocalsContainerBase, CompositeBase，ast.go:2465-2468）。

### 12.1 字段逐一映射

| 分组 | Go 字段（ast.go 行） | C# 映射 | 说明 |
|---|---|---|---|
| 工厂设定 | `fileName string`(2471)、`parseOptions SourceFileParseOptions`(2472)、`text string`(2473)、`Statements *NodeList`(2474)、`EndOfFileToken *TokenNode`(2475) | `private readonly string fileName; private readonly SourceFileParseOptions parseOptions; private readonly string text; public NodeList Statements; public Node EndOfFileToken;` | 访问器 `ParseOptions()/Text()/FileName()/Path()`（ast.go:2560-2574）→ 属性；`NewSourceFile` 的路径规范化断言（ast.go:2547-2549 panic）→ 异常 |
| 外部包惰性数据 | `dataMu sync.Mutex; data map[sourceFileDataKey]any`(2478-2479) | `private readonly Lock dataMu = new(); private Dictionary<ulong, object>? data;` | `SourceFileDataKey[T]`（2404-2420，泛型 key + 全局原子计数器）→ `public sealed class SourceFileDataKey<T> { internal readonly ulong Key = Interlocked.Increment(ref counter); }`；`sourceFileDataCell[T]{once, value}`（2413-2416）→ 每 cell 一个 `Lazy<T>` 惰性体：`GetOrComputeSourceFileData(file, key, compute)`（2422-2428）= dataMu 锁内取/建 cell（Lazy 包 compute），锁外 `.Value`（等价 once.Do 的阻塞语义） |
| parser 产物 | `diagnostics/jsDiagnostics/jsdocDiagnostics []*Diagnostic`(2482-2484)、`LanguageVariant/ScriptKind/IsDeclarationFile/ContainsNonASCII/UsesUriStyleNodeCoreModules`(2485-2489)、`Identifiers map[string]string`(2490)、`IdentifierCount int`、`imports/ModuleAugmentations/AmbientModuleNames/CommentDirectives`(2492-2495)、`ReparsedClones/Pragmas/ReferencedFiles/TypeReferenceDirectives/LibReferenceDirectives/CheckJsDirective/NodeCount/TextCount/CommonJSModuleIndicator/ExternalModuleIndicator`(2499-2510) | 同名字段/属性直译；`map[string]string` → `Dictionary<string, string>`（ordinal 键，07-aot） | Setter 型访问器（SetDiagnostics 等，2584-2610）原样保留方法 |
| JSDoc 缓存 | `jsdocCache map[*Node][]*Node`(2496)、`jsdocMu sync.RWMutex`(2497)、`hasLazyJSDoc bool`(2498) | `private Dictionary<Node, Node[]>? jsdocCache;`（**ReferenceEqualityComparer.Instance**，指针键保真）`private readonly ReaderWriterLockSlim jsdocMu = new();` | `resolveJSDoc`（2612-2637）双检锁直译：RLock 快路径 → Lock 慢路径 + 双检 + `parseJSDocForNode` 回调 |
| parser 包回调 | `parseJSDocForNode func(...)` + `SetParseJSDocForNode`（ast.go:17-24，parser init() 注册） | `internal static Func<SourceFile, Node, Node[]>? parseJSDocForNode;` + 静态 setter；parser 侧用 `[ModuleInitializer]` 注册（Go init 的 AOT 安全对应物，07-aot-constraints.md） | 断言未注册即 panic（2613-2615）→ 异常 |
| binder 产物 | `isBound atomic.Bool`(2514)、`bindOnce sync.Once`(2515)、`bindDiagnostics/BindSuggestionDiagnostics`(2516-2517)、`EndFlowNode *FlowNode`(2518)、`SymbolCount int`、`ClassifiableNames collections.Set[string]`、`PatternAmbientModules`、`GlobalExports SymbolTable`(2519-2522) | `private volatile bool isBound;` + `private Ts.Core.Once bindOnce;`；`IsBound()`（2749）→ `public bool IsBound => isBound;`；`BindOnce(bind)`（2765-2770）→ `bindOnce.Do(() => { bind(); isBound = true; });` | `Ts.Core.Once` = Lock+bool 的 sync.Once 直译（首个调用执行、并发调用阻塞至完成、之后零开销快路径 Volatile 读）——供全仓复用，01-architecture.md 归档 |
| 行图 | `ecmaLineMapMu sync.RWMutex; ecmaLineMap []core.TextPos`(2526-2527) | `private int[]? ecmaLineMap;` + `Lock`；`ECMALineMap()`（2700-2714）双检直译：Volatile 读命中即返回（数组引用发布安全），未命中锁内二次检查后计算 | RWMutex 在此仅保护一次性发布 → 降级为 Lock + Volatile（语义等价，Go 用 RLock 只为读多） |
| 语言服务 | `Hash xxh3.Uint128`(2531) → `public UInt128 Hash;`；`tokenCacheMu sync.Mutex; tokenCache map[TokenCacheKey]*Node`(2532-2533)；`tokenFactory *NodeFactory`(2534)；`declarationMapMu; declarationMap map[string][]*Node`(2535-2536)；`nameTableOnce sync.Once; nameTable map[string]int`(2537-2538) | `Lock` + `Dictionary<TokenCacheKey, Node>`；`TokenCacheKey{parent *Node; loc TextRange}`（2459-2462）→ `readonly record struct TokenCacheKey(Node Parent, TextRange Loc)`（Node 成员默认引用相等 = Go 指针键保真）；`nameTableOnce` → `Ts.Core.Once`；`GetNameTable`（2718-2747）/`GetOrCreateToken`（2774-2802，含 Kind 不匹配与 Reparsed panic→异常）/`createToken`（2805-2836 的 Kind switch）逐一直译 | Hash 由 program 层以 XxHash128 对 **UTF-8 字节**计算（主计划 §一.6 xxh3 保真分级） |
| UTF-8→UTF-16 映射 | `positionMapOnce sync.Once; positionMap *PositionMap`(2542-2543)、`GetPositionMap()`(2754-2763) | **整体删除**（主计划：一切位置即 UTF-16；PositionMap 仅服务 API/LS 线缆层） | `ContainsNonASCII` 字段保留（还被 GetPositionMap 之外的路径使用与否，移植 scanner/ls 时复核，§17-8） |

### 12.2 方法

`ForEachChild`（2647-2649）、`VisitEachChild`（2651-2653，走 `visitTopLevelStatements`）、`copyFrom`（2659-2678，注意**不**复制工厂设定字段 + `Flags |= other.Flags`）、`Clone`（2680-2685）、`computeSubtreeFacts`（2687-2689）、`UpdateSourceFile`（2691-2698）、`GetDeclarationMap/computeDeclarationMap`（2838-2955）、`IsJS`（2655）——全部手写直译进 `Ast/SourceFile.cs`。`SourceFileLike` 接口（2974-2977：Text+ECMALineMap）与 `HasFileName`（2454-2457）原样成接口。

---

## 13. 类型别名 → `global using` 清单（GlobalUsings.g.cs 生成）

Go 用类型别名把"多形态的 Node"命名化（全部 = Node / NodeList / Kind）。C# 用 `global using` 别名复刻，**生成单文件 GlobalUsings.g.cs**，经 Directory.Build.props 以 `<Compile Include>` 链接进 Ts.Compiler / Ts.LanguageService / Ts.Cli / 各测试项目（global using 是 per-compilation 的，必须每个项目都编入；工程细节归 04-codegen.md）。

五个家族（数量为 ast.json/生成物实测）：

| 家族 | 数量 | 来源 | 示例 |
|---|---|---|---|
| 节点别名 `XxxNode` | 192 | 每个节点定义（generate-go-ast.ts:786-790） | `global using IdentifierNode = Ts.Ast.Node;`、`global using TokenNode = Ts.Ast.Node;` |
| 实例化别名 | 39 | Token 33 个（EndOfFile、DotToken、QuestionToken、ExclamationToken、ColonToken、EqualsToken、AsteriskToken、EqualsGreaterThanToken、PlusToken、MinusToken、QuestionDotToken、AssertsKeyword、AssertKeyword、AwaitKeyword、CaseKeyword、AbstractKeyword、AccessorKeyword、AsyncKeyword、ConstKeyword、DeclareKeyword、DefaultKeyword、ExportKeyword、InKeyword、PrivateKeyword、ProtectedKeyword、PublicKeyword、ReadonlyKeyword、OutKeyword、OverrideKeyword、StaticKeyword、BinaryOperatorToken、AssignmentOperatorToken、DotDotDotToken）+ KeywordExpression 6 个（NullLiteral、TrueLiteral、FalseLiteral、ThisExpression、SuperExpression、ImportExpression） | `global using AsteriskToken = Ts.Ast.Node;` |
| list 别名 | 23 | listAliases（StatementList、CaseClausesList、VariableDeclarationNodeList、BindingElementList、TypeParameterList、ParameterList、HeritageClauseList、ClassElementList、TypeElementList、ExpressionWithTypeArgumentsList、EnumMemberList、ImportSpecifierList、ExportSpecifierList、TypeArgumentList、ArgumentList、TemplateSpanList、ElementList、PropertyDefinitionList、TypeList、ImportAttributeList、TemplateLiteralTypeSpanList、JsxChildList、JsxAttributeList） | `global using ParameterList = Ts.Ast.NodeList; // NodeList[*ParameterDeclaration]` |
| union/base 别名 | 72 | nodes.aliases（ast_generated.go:513-592 全表）：**Expression、Statement、TypeNode**、BlockOrExpression、NodeBody、AccessExpression、DeclarationName、ModuleName、ModuleExportName、PropertyName、ModuleBody、JSDocFullName、ForInitializer、ModuleReference、NamedImportBindings、NamedExportBindings、MemberName、EntityName、BindingName、ModifierLike、JsxChild、JsxAttributeLike、JsxAttributeName、JsxAttributeValue、JsxTagNameExpression、ClassLikeDeclaration、AccessorDeclaration、LiteralLikeNode、LiteralExpression、UnionOrIntersectionTypeNode、TemplateLiteralLikeNode、TemplateMiddleOrTail、TemplateLiteral、TypePredicateParameterName、ImportAttributeName、LeftHandSideExpression、JSDocComment、SignatureDeclaration、StringLiteralLikeNode、NumericOrStringLikeLiteral、ObjectLiteralLikeNode、ObjectTypeDeclaration、JsxOpeningLikeElement、NamedImportsOrExports、BreakOrContinueStatement、CallLikeExpression、FunctionLikeDeclaration、VariableOrParameterDeclaration、VariableOrPropertyDeclaration、CallOrNewExpression、ImportClauseOrBindingPattern、AnyImportSyntax、Declaration、ClassElement、TypeElement、ObjectLiteralElement、JSDocTag、ArrayBindingElement、AssertionExpression、BooleanLiteral、ConciseBody、DestructuringAssignment、LiteralToken、Modifier、ObjectLiteralElementLike、PropertyNameLiteral、PseudoLiteralToken、TemplateLiteralToken、ArrayDestructuringAssignment、ObjectDestructuringAssignment、FunctionBody、IncrementExpression | `global using Expression = Ts.Ast.Node; // Node with ExpressionBase` |
| ast.go 手写别名 | 7 | ast.go:1255-1263：NamedMember、AnyValidImportOrReExport、ValidImportTypeNode、TypeOnlyImportDeclaration、StringLiteralLike、ObjectLiteralLike、AnyImportOrRequireStatement | 归手写文件 GlobalUsings.cs（同机制） |
| kind 别名 | 34 | kinds.aliases（kind_generated.go 尾部）：TokenSyntaxKind、KeywordSyntaxKind、BinaryOperatorSyntaxKind… | `global using TokenSyntaxKind = Ts.Ast.SyntaxKind;`（保工厂签名形态 `NewToken(TokenSyntaxKind kind)`） |

注释保真：每条别名携带 Go 生成物同款注释（union 成员列表 / `Node with XxxBase`），这是移植者查"此处 Node 究竟是什么"的第一手册。

---

## 14. nil 接收者方法清单 → 扩展方法

审计方法：对 internal/ast 全部 .go 以正则 `func \((\w+) \*T\) M(...)  { if \1 == nil` 扫描方法体前 5 行。结果：**ast 包内恰 1 个**：

| Go 方法（file:line） | 接收者 | C# 形态 |
|---|---|---|
| `(node *ImportAttributesNode) GetResolutionModeOverride() (core.ResolutionMode, bool)`（ast.go:2215-2260，首句 `if node == nil { return core.ResolutionModeNone, false }`） | `ImportAttributesNode` = **Node 别名**（即 nil `*Node` 也可调用） | `public static class NodeExtensions { public static (ResolutionMode Mode, bool Ok) GetResolutionModeOverride(this Node? node) { if (node == null) return (ResolutionMode.None, false); ... } }`（多返回值→元组归 05-conventions.md 的 error/多返回规约） |

相关但**不属于**本清单的三类（避免误报，移植者注意）：

1. **nil-data 容忍**：`Node.Symbol()/LocalSymbol()/Locals()/Body()`（ast.go:240-270）——接收者非 nil，内部访问器结果判空 → 普通实例成员 + `?.`；
2. **nil 参数容忍**：`visit/visitNodes/visitNodeList/visitModifiers`（ast.go:30-58）、`NodeVisitor` 各方法 → 静态方法参数可空，无需扩展方法；
3. **别包的 nil 接收者**（checker/types.go:650,657 的 `TypeAlias.Symbol/TypeArguments`、collections/set.go 的 `Set[T]` 系列等，全仓共 134 处）→ 各归其模块文档（03-checker-design.md 等）；规则本身（"方法体以 nil 接收者检查开头 → C# 扩展方法"）固化在 05-conventions.md。

---

## 15. 内存布局对比估算（Go header+data vs C# 单对象）

计量口径：x64；Go 无对象头（GC 按 size class），C# 对象头 8B + 方法表指针 8B = 16B；两边指针均 8B；C# 字段由 CLR 自动重排对齐。

**Node 头部本身**：

| | Go（ast.go:179-186） | C# |
|---|---|---|
| Kind | int16 = 2（+2 pad） | short = 2 |
| Flags | uint32 = 4 | uint = 4 |
| Loc | 2×int32 = 8 | 2×int = 8 |
| id | atomic.Uint64 = 8 | ulong = 8 |
| Parent | 8 | 8 |
| **data 接口** | **16（itab+ptr）** | **0（不存在）** |
| 对象头 | 0 | 16 |
| 小计 | **48** | **48**（16 头 + 30 字段 + 2 pad） |

即：C# 的 16B 对象头开销恰好被删掉的 16B data 接口抵消，头部持平；节点越复杂 C# 越占优（死菱形副本 + 嵌套 struct 对齐 padding 消失）。

**代表节点全尺寸**：

| 节点 | Go 布局 | Go 总计 | C# 布局 | C# 总计 | Δ |
|---|---|---|---|---|---|
| Token（ast_generated.go:595-597） | Node 48 | **48** | 头 16 + 字段 32 | **48** | 0 |
| Identifier（:620-624） | Node 48 + FlowNodeBase 8 + Text string 头 16 | **72** | 头 16 + 字段 32 + FlowNode 8 + Text ref 8 | **64** | **−8** |
| FunctionDeclaration（:1990-1997，字段见 §3.2） | Node 48 + DeclB 8 + FlowNodeBase(经 StatementBase) 8 + ExportableB 8 + ModifiersB 8 + FLWBB(FLB 56 + BodyBase 24) 80 + CompositeB 8 + name 8 + ReturnFlowNode 8 | **184** | 头 16 + 头字段 32 + Symbol 8 + FlowNode 8 + LocalSymbol 8 + modifiers 8 + Locals 8 + NextContainer 8 + TypeParameters/Parameters/Type/FullSignature 32 + AsteriskToken/Body/EndFlowNode 24 + facts 4 + name 8 + ReturnFlowNode 8（+4 pad） | **176** | **−8** |
| MethodDeclaration（:3303-3311，§8.3） | Node 48 + NamedMemberB 32（含 DeclB 8）+ FLWBB 80（含**第二份** DeclB 8）+ FlowNodeB 8 + CompositeB 8 | **176** | 头 16 + 头字段 32 + §8.3 字段 116（Symbol 去重省 8）+ pad 4 | **168** | **−8** |

系统性结论：

- 每节点稳定省 **8–16B**（16B data 指针 − 16B 对象头 ± 死副本/padding）；对千万级节点的大项目即数百 MB 级差异的一部分。分配次数两边相同（Go 也是单分配；被删的 ~50 个 Arena 由 .NET gen0 bump 分配补位，主计划 §一.1）。
- string 计价差异要诚实标注：Go string 内联 16B（ptr+len），C# string 引用 8B 但堆上 string 对象自带 ~22B 头——**节点内**更省、全局取决于文本共享度（Identifier 文本经 SourceFile.Identifiers 表去重，两边同策略）。
- 性能风险（虚调用 vs Go 接口调用、经接口属性访问 mixin 字段的分发开销）→ 12-risks.md「GC vs arena 性能」条目观察项；阶段 7 的 AOT 性能门禁（≤2× tsgo）是硬校验。

---

## 16. 生成产物布局（≈5 个 .g.cs 的展开）与手写文件

| 文件 | 内容 | 约模 |
|---|---|---|
| `src/Ts.Compiler/Ast/SyntaxKind.g.cs` | `enum SyntaxKind : short`（351 元素 + Count + 34 markers）——对应 kind_generated.go | ~450 行 |
| `src/Ts.Compiler/Ast/Nodes.g.cs` | 13 mixin 接口 + 4 brand 接口 + 17 抽象 base 类 + 191 节点类声明（字段 + partial） | ~4,500 行 |
| `src/Ts.Compiler/Ast/Accessors.g.cs` | mixin 虚访问器 override（§8.2 全表）+ `Name()` 私有访问器（34 处）+ CompositeBase 缓存 worker（72 处）+ TypeSyntaxBase 行为再安置（§3.4） | ~1,800 行 |
| `src/Ts.Compiler/Ast/NodeCasts.g.cs` | Node partial：As* ×192、Is* ×232、kind 守卫 ×33 | ~1,600 行 |
| `src/Ts.Compiler/Ast/NodeFactory.g.cs` | New* ×193、Update* ×166 | ~3,500 行 |
| `src/Ts.Compiler/Ast/Visitors.g.cs` | ForEachChild ×167、VisitEachChild ×166、Clone ×191、生成的 computeSubtreeFacts ×38 | ~2,800 行 |
| `src/Ts.Compiler/Ast/GlobalUsings.g.cs` | §13 五族别名 ×360+（含 kind 别名） | ~380 行 |
| 手写：`Ast/Node.cs`（头 + 虚方法默认 + 便捷访问器）、`Ast/Node.Accessors.cs`（§4.4 switch 访问器）、`Ast/Ast.cs`（ast.go 其余手写：mixin 语义方法、subtree facts 手写实现、GetDeclarationFromName 等工具）、`Ast/NodeList.cs`、`Ast/SourceFile.cs`、`Ast/Visitor.cs`、`Ast/Utilities.cs`、`Ast/Ids.cs`、`Ast/Flow.cs` 等 | 对应 ast.go / utilities.go / visitor.go / flow.go … 的文件 1:1 | — |

物理文件为 7 个 .g.cs，是主计划「~5 个」的展开（新增 SyntaxKind 与 GlobalUsings 两个天然独立单元）。**本布局为定稿（评审裁决 R2：主计划「~5 个」为约数，以 7 为准），04/05/06/10 已同步**，见 §17-2。所有类 `partial`，生成物入库提交（不用 Roslyn 源生成器，理由见主计划 §一.1）。再生成命令、dprint→`dotnet format` 对应物、ast.json 副本同步流程归 04-codegen.md。

验收（与 08-testing-strategy.md 阶段 3 门禁衔接）：`Ast/*.g.cs` 通过 AOT+Trim analyzer（阶段 0 门禁）；AST 结构转储与 dumptool 夹具 diff 100%（阶段 3 门禁）——生成器错一处，全语料立刻暴露。

---

## 17. 待澄清

1. **As* 计数**：主计划写 "~190 个 As* / ×190"，实测生成 **192**（每节点 1 个）+ 2 个手写 flow-data 转换（ast.go:1171-1177）。按实测执行，不视为矛盾（主计划为约数），建议 11-progress.md 以 192 计。
2. **.g.cs 文件数**（已关闭，裁决 R2）：主计划「产物拆 ~5 个 .g.cs（Nodes/NodeCasts/NodeFactory/Visitors/Accessors）」，本档展开为 7 个物理文件（+SyntaxKind.g.cs、GlobalUsings.g.cs）。评审裁决 R2：§16 的 7 文件布局为定稿，主计划「~5 个」是约数，以 7 为准；04/05/06/10 已同步。
3. **`SubtreeFacts()` 方法与 `SubtreeFacts` 枚举同名**（已关闭，裁决 R9）：C# 合法但在类内引用类型名时需 `Ts.Ast.SubtreeFacts` 限定，易读性差。评审裁决 R9：方法与枚举同名保留（C# 允许成员名=返回类型名，Color-Color 规则），不改名；见 05-conventions.md 对应条目。
4. **mixin 字段经接口属性访问的分发开销**：`node.FunctionLikeData().Type` 在 Go 是指针+字段偏移，在 C# 是虚调用+接口属性调用。若 checker 热路径出现可测退化，对策是调用点局部改具体类型直访（不改设计）。请 12-risks.md 增列观察项，阶段 7 性能门禁兜底。
5. **非 mixin 接口的 I 前缀**：`NodeFactoryCoercible`、`SourceFileLike`、`HasFileName` 不是 mixin，主计划的"不加 I 前缀"例外字面上只覆盖 mixin 接口。倾向对 ast 包内全部 Go 接口统一不加前缀以保 grep 一致，请 05-conventions.md 拍板。
6. **IterChildren 的惰性丢失**：Go iter.Seq 支持消费方早停即停止遍历；C# 缓冲实现会先收集全部孩子。正确性等价（visitor 无副作用），最坏多遍历一层的孩子集合。若剖析命中热点，可引入基于 `ForEachChild` 的 struct enumerator。暂按缓冲实现。
7. **`ReleaseArenas()`（ast.go:3053-3059）**：删 Arena 后该 API 语义为空。建议删除并同步清理调用点；调用点分布（parser 复用工厂处）待 06-module-inventory.md 移植 parser 时核对。
8. **`ContainsNonASCII` 字段**：其主要消费者 GetPositionMap 已随 PositionMap 删除；该字段是否仍被 scanner 快路径/LS 引用需在阶段 2/8 移植时复核后决定去留（先保留）。
9. **`modifierFlags`（ModifiersBase 的 noGo 字段）与 `FunctionLikeWithBodyBase.Body`（noGo 窄化）**：两者均为 TS-only schema 内容，Go/C# 都不生成；生成器必须实现 `noGo`/`goOnly`/`noTS`/`noFactory` 四标志的同款过滤（schema.ts:649-666），否则字段表会悄悄多出 TS 专属成员。此为实现注意事项而非分歧。
