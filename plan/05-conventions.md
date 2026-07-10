# 05 — Go→C# 惯用法圣经（Conventions）

> **版本** v1.0 ｜ **日期** 2026-07-11 ｜ **状态** Draft
>
> 本文档是全项目最高频查阅的移植规则手册。逐函数直译时遇到的每一类 Go 惯用法，先查本文；本文没有的，先在 06-module-inventory.md 对应包的"危险点"栏查，再没有的按第 0 节的总原则裁量并**回填本文**（活文档，PR 中同步更新）。
>
> 所有 Go 代码示例除特别注明外均摘自 `/Users/token/Desktop/code/typescript-go-main`（下文以 `tsgo:` 前缀引用相对路径），行号以主计划钉扎的 commit 为准。架构级决策（AST 继承体系、Checker 类型树、并发模型、UTF-16、AOT 铁律）以主计划及 01/02/03/07 号文档为准，本文只做"翻译层"规则，不新设架构。

---

## 0. 总原则（裁量时的优先序）

当具体规则之间冲突、或遇到本文未覆盖的形态时，按以下优先序裁量：

1. **输出字节保真压倒一切**。任何影响 conformance 基线字节（错误消息文本、数字格式、排序、哈希 hex、JSON 形态）的翻译，必须复刻 Go 的可观察行为，哪怕写出不地道的 C#（参见 08-testing-strategy.md）。
2. **逐函数直译、控制流保形**。同名函数、同序语句、同构分支。review 时应能把 C# 文件和 Go 文件左右并排逐段对照。
3. **C# 惯例命名，Go 拼写可 grep**。命名转换是机械规则（§1），不做"意译"；重命名以"去掉 Go 式前缀、加上 C# 大小写"为限。
4. **能在类型系统里消失的差异就让它消失**（typed-nil、header+data 拆分、PositionMap），**不能消失的差异必须留下书面标注**（map 迭代序、int 宽度、NaN 键），标注格式见 §7.3 与 §17.4。
5. **刻意偏离 Go 的每一处**，在 C# 侧留 `// PORT-NOTE: <原因>` 注释，并在 06-module-inventory.md 的对应包条目登记。

---

## 1. 命名

### 1.1 可见性：exported/unexported → public/private

| Go | C# | 说明 |
|---|---|---|
| 首字母大写（exported） | `public` | 跨包使用的即公共 API |
| 首字母小写（unexported） | `private`（同类内）/ `internal`（同包跨文件、跨类使用） | Go 的 unexported 是**包**级可见，C# 没有"命名空间私有"；同一 Go 包内跨文件使用的小写符号译为 `internal` |
| `internal/` 目录整体 | 程序集内 `public` + 不做 API 承诺 | tsgo 全部代码都在 `internal/` 下；C# 侧以程序集边界（Ts.Compiler / Ts.LanguageService）代替，不额外用 `[InternalsVisibleTo]` 之外的机制收紧 |

**一切标识符（含 private 方法）都转 PascalCase**——C# 惯例中方法/属性/类型不因可见性改变大小写。示例（tsgo:internal/checker/checker.go:16406 与 tsgo:internal/checker/exports.go:176）：

```go
// Go：小写 = 包内私有
func (c *Checker) getTypeOfSymbol(symbol *ast.Symbol) *Type { ... }
// Go：大写 = 导出（exports.go 中的薄包装）
func (c *Checker) GetTypeOfSymbol(symbol *ast.Symbol) *Type { ... }
```

```csharp
// C#：两者都是 PascalCase，靠可见性修饰符区分。
// 同名冲突时（如上例 私有实现+公共包装 同名），私有侧加 Core 后缀。
private Type GetTypeOfSymbolCore(Symbol symbol) { ... }   // 原 getTypeOfSymbol
public Type GetTypeOfSymbol(Symbol symbol) { ... }         // 原 GetTypeOfSymbol（exports.go）
```

> **同名冲突规则**：Go 里 `getFoo`/`GetFoo` 可共存，C# 转 PascalCase 后撞名。裁定：**导出者保留原名**（它是被外部依赖、被文档引用的名字），非导出者加 `Core` 后缀。全项目统一用 `Core`，不用 `Internal`/`Impl` 混用。

**字段与局部变量**：

| Go | C# | 示例 |
|---|---|---|
| 导出字段 `Kind Kind` | `public` **字段**（不自动属性化），PascalCase | `public SyntaxKind Kind;`（主计划 §一.1 样例即公共字段） |
| 非导出字段 `id atomic.Uint64` | `private`/`internal` 字段，**camelCase、不加 `_` 前缀** | `internal ulong id;`（主计划 §一.1 原文如此） |
| 局部变量/参数 | camelCase，与 Go 同拼写 | `sourceFile`, `pos`, `ch` |
| 包级 var | `static` 字段，可见性同上规则 | `nextCheckerID atomic.Uint32`（tsgo:internal/checker/checker.go:578）→ `private static uint nextCheckerId;` |
| 常量 `const foo = ...` | `const`/`static readonly`，PascalCase（导出与否只影响可见性） | `checkerHeldAnonymous`（tsgo:internal/project/checkerpool.go:20）→ `private const string CheckerHeldAnonymous = "<anonymous>";` |

> **裁定：私有字段不加 `_` 前缀**。理由：(a) 主计划样例 `internal ulong id;` 已如此；(b) 保持与 Go 源拼写逐字符对照，`grep enumLiteralTypes` 双仓同中；(c) 冲突时（字段与参数同名）用 `this.` 消歧，与 Go 的 `c.foo` 视觉对应。需要在阶段 0 的 `.editorconfig` 中关闭 IDE1006 的下划线建议（见文末待澄清 #5）。

**枚举成员去前缀**。Go 没有枚举作用域，用类型名做前缀；C# 枚举自带作用域：

```go
// tsgo:internal/ast/kind.go 与 tsgo:internal/checker/checker.go:25275
ast.KindIdentifier          TypeFlagsEnumLiteral | TypeFlagsStringLiteral
```

```csharp
SyntaxKind.Identifier       TypeFlags.EnumLiteral | TypeFlags.StringLiteral
```

同理 `ModifierFlagsAmbient`→`ModifierFlags.Ambient`、`ComparisonLessThan`（tsgo:internal/stringutil/compare.go:29）→`Comparison.LessThan`。注意 `ast.Kind` 类型本身改名 `SyntaxKind`（主计划 §一.1 钦定，与上游 TS 名称一致）。**这是全文唯一允许的"类型改名"，其余类型名一律保 Go 原名**（只调大小写）。

### 1.2 Go getter 方法 → C# 属性的判据

Go 没有属性，一切都是方法。C# 侧按下表判定：

| 判据（全部满足才属性化） | 属性 | 保持方法 |
|---|---|---|
| 无参数 | ✔ | 有参数 → 方法 |
| 纯读：不推进状态、不加锁、不做惰性计算 | ✔ | 有副作用 → 方法 |
| O(1) 或近似（简单字段读/一次虚分派） | ✔ | 遍历/分配/开销不定 → 方法 |
| 对任意合法状态都不抛（断言除外） | ✔ | 对部分 Kind panic → 方法 |
| 未被主计划/生成器清单钦定为方法 | ✔ | 02 已定型签名 → 从 02 |

**属性化案例**（tsgo:internal/core/text.go:22-32；tsgo:internal/ast/ast.go:193-194；tsgo:internal/checker/types.go:676）：

```go
func (t TextRange) Pos() int { return int(t.pos) }
func (t TextRange) Len() int { return int(t.end - t.pos) }
func (n *Node) Pos() int     { return n.Loc.Pos() }
func (t *Type) Id() TypeId   { ... }
```

```csharp
public readonly record struct TextRange(int Pos, int End) {
    public int Len => End - Pos;
}
public abstract class Node {
    public int Pos => Loc.Pos;
    public int End => Loc.End;
}
public TypeId Id => id;   // Type 上的只读暴露
```

**保持方法的案例**：

```go
// tsgo:internal/scanner/scanner.go:469 —— 推进扫描器状态：方法
func (s *Scanner) Scan() ast.Kind { ... }

// tsgo:internal/ast/ast.go:2701 —— 惰性计算 + RWMutex：方法
func (node *SourceFile) ECMALineMap() []core.TextPos { ... }

// tsgo:internal/ast/ast.go:272 —— 20 路 switch，未覆盖 Kind 会 panic：方法
func (n *Node) Text() string { ... }

// 主计划 §一.1 钦定为方法的 mixin 访问器（虚分派 + 常态返回 null）：
public virtual DeclarationBase? DeclarationData() => null;
```

灰区速判：`Node.Symbol()`（tsgo:internal/ast/ast.go:240，经 DeclarationData 的一次虚分派 + null 检查，纯读 O(1)）→ **属性**；`NodeList.Pos()/End()`（ast.go:135-136）→ 属性；`SourceFile.GetNameTable()`（ast.go:2718，Once 惰性构建整表）→ 方法。**动词开头的名字（Get/Compute/Resolve/Ensure）一律方法**，即使碰巧便宜——名字即契约。

**同形保留两例（裁决 R9）**：`Node.AsMutable()`（tsgo:internal/ast/ast.go:237，一行重解释转换）→ **保留同名一行方法**（不属性化），以保 `node.AsMutable().SetType(...)`（reparser.go:350 等）调用点形态；`Node.SubtreeFacts()`（ast.go:220，方法名 = 返回类型名 `SubtreeFacts`）→ **方法与枚举同名原样保留**——C# 允许成员名与其类型名相同（Color-Color 规则），不改名。

### 1.3 接口命名与 mixin 例外

| 类别 | 规则 | 示例 |
|---|---|---|
| 常规行为接口 | 加 `I` 前缀（C# 惯例） | `vfs.FS`（tsgo:internal/vfs/vfs.go:12）→ `IFileSystem`（`IFS` 不可读，此处允许展开缩写）；`HasTextDocumentURI`（tsgo:internal/lsp/lsproto/lsp.go:66）→ `IHasTextDocumentUri` |
| **13 个数据 mixin 接口** | **不加 `I` 前缀**（主计划 §一.1 钦定例外，保 grep 对照） | `DeclarationBase`、`FlowNodeBase`、`FunctionLikeBase` … 原名即接口名 |
| **4 个无字段 brand 标记接口** | 同上，不加 `I` | `TypeSyntaxBase`、`TypeElementBase`、`ClassElementBase`、`ObjectLiteralElementBase` |
| 接口 + 封闭实现集 + 工厂函数 | 可译为**抽象类保原名**（免去 I 前缀问题，且能带实现） | `core.WorkGroup`（tsgo:internal/core/workgroup.go:11，仅 parallel/singleThreaded 两实现 + `NewWorkGroup` 工厂）→ `abstract class WorkGroup` + `static WorkGroup Create(bool singleThreaded)`；主计划行文即用无前缀 `WorkGroup` |

`var _ WorkGroup = (*parallelWorkGroup)(nil)`（workgroup.go:32）这类编译期接口实现断言，C# 编译器天然保证，**直接删除**。

### 1.4 缩写词大小写

采用 .NET 框架设计准则：**两字母缩写全大写，三字母及以上 Pascal 化；Id/Ok 视为普通单词**。

| Go 拼写 | C# 拼写 | 出处示例 |
|---|---|---|
| `ID`（类型名） | `Id` | `jsonrpc.ID`（tsgo:internal/jsonrpc/jsonrpc.go:32）→ `JsonRpc.Id`；`TypeId`（types.go:157）保持 `TypeId`；`nextCheckerID` → `nextCheckerId` |
| `JSON` | `Json` | `jsonrpc` 包 → `Ts.JsonRpc` 命名空间 |
| `URL` / `URI` | `Url` / `Uri` | `TextDocumentURI()` → `TextDocumentUri`；`DocumentUri`（lsproto/lsp.go:17）原样 |
| `UTF16` / `UTF8` | `Utf16` / `Utf8` | `core.UTF16Len`（core.go:486）→ `Utf16Len`（若保留，见 §8.2） |
| `LSP` / `AST` / `JSX` / `ECMA` | `Lsp` / `Ast` / `Jsx` / `Ecma` | `ECMALineMap` → `EcmaLineMap` |
| `JSDoc` | `JSDoc`（专名例外，整体保留） | `IsJSDocTypeAssertion`、`AsJSDocText`、`JSDocTagBase` 原样（见下方裁定） |
| `IO` / `OS`（两字母） | `IO` / `OS` 全大写 | 与 `System.IO` 一致 |
| `NaN` | `NaN` | 专名不动：`IsNaN`、`enumNaNLiteralTypes` |

> JSDoc 系列注意：Go 源里 `JSDoc` 出现于 ~百个标识符（`AsJSDocText` 等）。**改判（裁决 R6）：`JSDoc` 专名整体保留**，不适用三字母缩写规则——理由：(a) 双仓 grep 对照性；(b) 02-ast-design.md 全部生成面示例即用 `JSDoc`；(c) 与 Go 实名一致。文件名 `Checker.JSDoc.cs`/`JSDoc.cs`，标识符 `JSDocTagBase`、`AsJSDocText`、`IsJSDocKind` 等原样。`JSDoc` 记入**专名例外清单**（与 §1.3 的 mixin 无 `I` 前缀例外并列）；生成器（04-codegen.md 的 csharp-names.ts）规格显式写"JSDoc 原样保留"，避免手写不一致。

### 1.5 包名 → 命名空间

`internal/<pkg>` → `Ts.<Pkg>`（主计划 §三：`internal/ast`→`Ts.Ast`）。多词包名 Pascal 连写：`tspath`→`Ts.TsPath`、`jsnum`→`Ts.JsNum`、`stringutil`→`Ts.StringUtil`、`tsoptions`→`Ts.TsOptions`、`modulespecifiers`→`Ts.ModuleSpecifiers`、`jsonrpc`→`Ts.JsonRpc`。物理归属（哪个 csproj）见 06-module-inventory.md。Go 调用点 `core.Filter(...)` → C# `Core.Filter(...)`（静态类 `Ts.Core.Core` 太拗口：包级函数集中到与包同名的静态类，如 `Ts.Core` 命名空间下 `static class Core`；文件级函数多的包可按 Go 文件分静态类，见 §2）。宿主类钦定一例（裁决 R7）：ast 生成的 `Is*` 节点守卫统一落 `Ts.Ast.NodeTests` 静态类——对应原版 TypeScript 的 `factory/nodeTests.ts`，域忠实。

---

## 2. 文件映射（1:1）

### 2.1 基本规则

| Go | C# | 示例 |
|---|---|---|
| 一个 `.go` 文件 | 一个同名 `.cs` 文件，snake_case→PascalCase | `internal/scanner/scanner.go` → `src/Ts.Compiler/Scanner/Scanner.cs`；`sourcemap_recorder.go` → `SourceMapRecorder.cs`；`ordered_map.go` → `OrderedMap.cs` |
| 包级函数（不属于任何类型） | 收进静态类；单文件包用与包同名的静态类，多文件包按文件名建静态类 | `core/core.go` 的 `Filter/Map/SameMap` → `Core/Core.cs` 内 `public static partial class Core`；`core/binarysearch.go` → `Core/BinarySearch.cs` 内 `partial class Core`（同一静态类跨文件 `partial`，调用点统一 `Core.X`） |
| `*_generated.go` | `*.g.cs`，由 tools/generator 产出并入库 | `ast_generated.go` → 定稿拆 7 个（裁决 R2，02-ast-design.md §16 权威）：`Ast/Nodes.g.cs`、`NodeCasts.g.cs`、`NodeFactory.g.cs`、`Visitors.g.cs`、`Accessors.g.cs`、`SyntaxKind.g.cs`、`GlobalUsings.g.cs`（这是**唯一**打破 1:1 的刻意拆分，04-codegen.md 管辖） |
| `*_test.go` | 测试项目镜像目录下 `*Tests.cs` | `tspath/path_test.go` → `tests/Ts.Compiler.Tests/TsPath/PathTests.cs`（§16） |
| 构建标签/平台后缀文件（`_darwin.go`、`_windows.go`、`realpath_darwin.go`） | 无直接等价。逐案：运行时分支（`OperatingSystem.IsWindows()`）或单文件内 `#if` 分区；fswatch 整包按主计划用 FileSystemWatcher 重写，不逐文件对映 | `nativepath/realpath_darwin.go`+`realpath_windows.go` → `NativePath/RealPath.cs` 运行时分支 |
| `doc.go`（纯包注释） | 并入该目录首文件头注释，或删 | — |

**文件内部结构**：file-scoped namespace；类型顺序、方法顺序与 Go 文件**保持一致**（这是并排 review 的前提）；Go 文件顶部的 `import` 组 → `using` 组（`ImplicitUsings` 关闭，主计划 §三，一切 using 显式写出）。

### 2.2 巨文件 partial 拆分

`>15k` 行的 Go 文件（checker.go 31,926 行为最）拆 `partial class`，**拆分边界 = Go 原有分节注释**，文件命名 `Checker.<Section>.cs`（`Checker.cs` / `Checker.Expressions.cs` / `Checker.Instantiation.cs` / …）。**Checker 的 22 个 partial 文件拆分表以 03-checker-design.md §4.2 为唯一权威（裁决 R1）**，checker 包内 checker.go 之外文件的命名亦采 03 风格（`Checker.Flow.cs`、`Checker.Relater.cs`、`Checker.JSDoc.cs`、`NodeBuilder.Hover.cs` 等）；其余巨文件的逐文件拆分表登记在 06-module-inventory.md 的对应包条目。拆分只影响物理文件，不影响类型结构；每个分片文件头注明来源区间：

```csharp
// Ported from tsgo internal/checker/checker.go (sections: Type relations, lines ~9000-14500)
public partial class Checker {
```

### 2.3 类型别名与命名类型

| Go 形态 | C# 移植 | 示例 |
|---|---|---|
| 纯别名 `type Comparison = int`（tsgo:internal/stringutil/compare.go:26） | `global using`（集中在各项目 `GlobalUsings.cs`） | `global using Comparison = int;` |
| AST 语义别名 `type Expression = Node` | 主计划 §一.1：`global using Expression = Ts.Ast.Node;`（生成器产出） | — |
| 命名类型带方法 `type Number float64`（tsgo:internal/jsnum/jsnum.go:20） | `readonly record struct` 包装 + 运算符重载 | `public readonly record struct Number(double Value)`（§9） |
| 命名类型带方法 `type DocumentUri string`（lsproto/lsp.go:17） | `readonly record struct DocumentUri(string Value)` | 方法照搬 |
| 命名集合 `type SymbolTable map[string]*Symbol`（tsgo:internal/ast/symbol.go:45） | 无方法且 nil 语义简单 → `global using SymbolTable = System.Collections.Generic.Dictionary<string, Ts.Ast.Symbol>;`；若 nil 读语义被利用（§7.2）→ 配 null 容忍扩展方法 | — |
| 命名切片 `type ECMALineStarts []TextPos`（core.go:428） | C# 12 别名可指数组：`global using EcmaLineStarts = Ts.Core.TextPos[];` | — |

---

## 3. 类型宽度

### 3.1 核心事实与全局规则

**Go 的 `int`/`uint` 在本项目全部目标平台上是 64 位；C# 的 `int` 是 32 位。** 逐字翻 `int`→`int` 隐含一次窄化，必须按语义分流：

| Go 用途 | C# 类型 | 判据与证据 |
|---|---|---|
| 源文本位置/长度/token 偏移 | `int` | tsgo 自己就把位置压成 32 位：`type TextPos int32`（tsgo:internal/core/text.go:5），`TextRange{pos, end TextPos}`。源文件 < 2 GiB 是上游不变量。C# 侧一律裸 `int`，不引入 TextPos 包装（string 索引本来就是 int） |
| 集合长度/索引/单文件内计数 | `int` | `len()` 语义等价 `.Length`/`.Count`；C# 集合本就 int 上限 |
| **跨 Program 聚合计数器**（全项目 identifier 总数、字节吞吐统计、tracing 计数） | `long` | Go 的 int 是 64 位所以从不溢出；C# int 在巨型 monorepo 上有理论溢出面。判据：**累加对象是"多个文件之和"即用 long** |
| 文件大小/时间戳 | `long` | Go 侧本来就是 `int64`（`fs.FileInfo.Size()`）；`atomic.Int64`（tsgo:internal/core/bfs.go:87）→ `long` + Interlocked |
| 哈希值 | `ulong` / `UInt128` | xxh3 64 位 → `ulong`；`xxh3.Uint128` → `UInt128`（主计划 §一.6 `CacheHashKey`） |
| 容量提示 | `int`（.NET API 签名如此） | `make([]T, 0, n)` 的 n → `new List<T>(n)` |

### 3.2 定宽类型直映射

| Go | C# | 示例 |
|---|---|---|
| `int8`/`int16`/`int32`/`int64` | `sbyte`/`short`/`int`/`long` | `KindValue() int16`（tsgo:internal/ast/ast.go:227）→ `short`；`SyntaxKind` 枚举底型 `short`（主计划 §一.1） |
| `uint8(byte)`/`uint16`/`uint32`/`uint64` | `byte`/`ushort`/`uint`/`ulong` | `TypeId uint32`（tsgo:internal/checker/types.go:157）→ `uint`；`NodeFlags` → `[Flags] enum NodeFlags : uint`；`id atomic.Uint64`（ast.go:183）→ `ulong` + `Interlocked.CompareExchange` |
| `float64` | `double`（经 `JsNum.Number` 包装参与 JS 语义运算，§9） | tsgo:internal/jsnum/jsnum.go:20 |
| `rune` | 上下文二选一：解码/遍历用 `System.Text.Rune`；与码点常量比较、表驱动查表用 `int`（码点值） | `charAndSize() (rune, int)`（scanner.go:441）→ `(Rune ch, int size)` 或热路径 `(int ch, int size)`；`ch >= 0x80` 之类比较用 int 更顺 |
| `byte`（UTF-8 文本字节） | **通常消失**：UTF-16 移植后 `s[i]` 是 `char`（§8.1）；真字节处理（输出层、哈希入料）用 `byte` | — |
| `uintptr`/`unsafe.Pointer` | 仅存在于 fswatch FFI（重写）与 vfs 零拷贝（§15），不直译 | — |

### 3.3 溢出语义与审计点

- **不开启** `/p:CheckForOverflowUnderflow`。Go 整数运算是环绕语义，与 C# 默认 unchecked 一致；开启 checked 反而制造行为分歧（哈希混合、`ch + ('a'-'A')` 之类刻意环绕）。
- Go `TextPos(pos)`、`int32(x)` 显式窄化 → C# `(int)`/`(short)` 显式转换原样保留，**不加范围检查**（Go 转换也是截断语义）。
- jsnum 的 `toInt32/toUint32`（tsgo:internal/jsnum/jsnum.go:44-71）是 ECMA 语义的刻意实现，**逐行照搬**，不用 `Convert.ToInt32`（会抛/会舍入）。
- **审计点清单**（移植该处代码时核对，登记入 12-risks.md）：
  1. 跨文件聚合计数器（判据见 §3.1 表）——移植点逐个升 `long`；
  2. `pos * 2`、`pos + len` 形式的位置算术——单文件 <2 GiB 前提下安全，不动；
  3. 位移量：Go `x << s` 对 s≥64 有定义（结果 0），C# 位移按位宽取模（`1 << 32 == 1`）！凡位移量非常量的点必须核对上界（jsnum `toShiftCount` 已 `& 31`，安全）；
  4. `int(uintN)` 混合比较：Go 不允许隐式混符号比较，源码已显式转换，照搬即可。

---

## 4. error 返回 → C# 的三分映射

### 4.1 判定总表

Go 的多返回值错误处理按**调用点消费形态**三分：

| Go 形态 | 判据 | C# 移植 |
|---|---|---|
| `(T, ok bool)` | "可失败查询"，失败是常态路径 | **Try 模式**：`bool TryX(args, out T result)` |
| `(T1, T2)` 非 ok 语义 | 纯多返回值 | **元组**：`(T1 A, T2 B) X(args)` |
| `(T, error)`，错误就地消费（`if err != nil` 后转诊断/忽略/降级） | 错误不跨层 | Try 模式；**若 err.Error() 文本进入诊断/基线** → `bool TryX(args, out T result, out string? errorText)` 且错误文本逐字复刻 Go 格式 |
| `(T, error)`，错误跨 ≥2 层传播（`fmt.Errorf("%w")` 链、直达 main） | 错误即异常路径 | **抛异常**（IOException/InvalidOperationException 派生），在 Go 的最终消费点 catch |
| 仅 `error` 返回 | 同上两行判据 | 就地消费 → `string? X(args)` 返回错误消息（null=成功）或 Try；跨层 → `void X(args)` + 异常 |
| `panic(...)` | 不变量违反 | 抛异常（§4.3） |

### 4.2 真实案例

**(T, ok) → Try**（tsgo:internal/vfs/vfs.go:20-22 与 tsgo:internal/semver/version_range.go:143）：

```go
// ReadFile reads the file specified by path and returns the content.
// If the file fails to be read, ok will be false.
ReadFile(path string) (contents string, ok bool)

func TryParseVersionRange(text string) (VersionRange, bool)
```

```csharp
bool TryReadFile(string path, out string contents);          // IFileSystem 成员
public static bool TryParseVersionRange(string text, out VersionRange range);
```

调用点 `if contents, ok := fs.ReadFile(p); ok { ... }` → `if (fs.TryReadFile(p, out var contents)) { ... }`。

**非 ok 多返回值 → 元组**（tsgo:internal/scanner/scanner.go:441）：

```go
func (s *Scanner) charAndSize() (rune, int)
```

```csharp
private (Rune Ch, int Size) CharAndSize()
```

**(T, error) 就地消费 → Try**（tsgo:internal/module/resolver.go:1782）：

```go
packageJsonContent, err := packagejson.Parse([]byte(contents))
if err == nil { ... }   // 失败即当没有 package.json，err 文本不外泄
```

```csharp
if (PackageJson.TryParse(contents, out var packageJsonContent)) { ... }
```

**(T, error) 且 error 信息可能承重**（tsgo:internal/semver/version.go:210 `TryParseVersion(text) (Version, error)`）：移植前先 grep 全部调用点确认 error 文本是否被打印/进诊断；semver 此处调用点只判空 → 降级为 `bool TryParseVersion(text, out Version v)`，error 文本丢弃并留 `// PORT-NOTE: Go error text unused by callers`。

**error 跨层 → 异常**：`vfs.FS.WriteFile/Remove/Chtimes` 返回 error（vfs.go:23-32），错误经 Program/execute 层层上traversal 后转 CLI 诊断。移植：C# `void WriteFile(...)` 抛 `IOException`，在 Go 侧做 `err != nil → 诊断` 的那一层 `catch (IOException e)`，用 `e.Message` 进诊断——**并核对该消息是否进基线，若进，需以 Go 的 `%s: %v` 格式手工拼接**（登记 12-risks.md，见待澄清 #4）。

### 4.3 panic / recover → 异常

| Go | C# | 示例 |
|---|---|---|
| `panic("message")`（不变量违反） | `throw new InvalidOperationException("message")`，消息原文保留 | tsgo:internal/parser/parser.go:914 `panic("Unhandled case in isListElement")` → `throw new InvalidOperationException("Unhandled case in isListElement");` |
| `panic(fmt.Sprintf(...))` | 同上 + 字符串插值 | tsgo:internal/ast/ast.go:303 `panic(fmt.Sprintf("Unhandled case in Node.Text: %T", n.data))` → `throw new InvalidOperationException($"Unhandled case in Node.Text: {GetType().Name}");` |
| `debug.Fail(reason)`（tsgo:internal/debug/debug.go:7-15，**生产构建也 panic**） | 自建 `Ts.Core.Debug.Fail(string)`——**always-on、抛异常**。绝不可用 `System.Diagnostics.Debug.Assert`（Release 下被剥除，行为分歧） | `debug.AssertNever(member)` → `Ts.Core.Debug.AssertNever(member)` |
| 失败类型断言 `x.(T)` / `node.AsIdentifier()` | 直接强转 `(T)x`，`InvalidCastException` 对应 panic（主计划 §一.1） | — |
| 越界 `s[i]` panic | C# `IndexOutOfRangeException`，天然对应，无需处理 | — |
| `recover()` 于服务边界 | `catch (Exception e)` 于同一边界 | tsgo:internal/lsp/server.go:997 请求处理 recover → LSP 请求 dispatch 外层 `try { ... } catch (Exception e) { /* 同样转 LSP 错误响应 */ }` |
| `recover()` 于测试 | `Assert.Throws<T>`（§16.4） | tsgo:internal/testutil/testutil.go:14 `AssertPanics` |

注意事项：
- Go 的 recover 能接住**一切** panic（含 runtime 错误如空指针解引用）；C# `catch (Exception)` 同样接住 NRE/ICE——语义对齐。但 **StackOverflowException 不可捕获**：Go 协程栈自动增长掩盖的深递归在 C# 是进程死亡。这就是 LargeStackExecutor 是 0 号风险对策的原因（主计划 §一.5、07-aot-constraints.md）。恢复边界（LSP/API/project 层的 recover 点）不指望 catch 栈溢出，**指望 64MB 大栈让它不发生**。
- panic 值的**类型**在 tsgo 恢复点偶有区分（`_ = recover()` 只吞不看，tsgo:internal/checker/tracer.go:343）——吞掉型 recover → `catch (Exception) { }` 空捕获 + `// PORT-NOTE` 注明源出处。

---

## 5. defer → try/finally / using / Deferred

### 5.1 四种形态

**① `defer mu.Unlock()` 紧跟 `Lock()`** → `lock` 语句（`System.Threading.Lock`，主计划 §一.5）：

```go
// tsgo:internal/core/workgroup.go:61-63
w.fnsMu.Lock()
defer w.fnsMu.Unlock()
w.fns = append(w.fns, fn)
```

```csharp
lock (fnsLock) {           // fnsLock 是 System.Threading.Lock 字段
    fns.Add(fn);
}
```

**② 一般清理 defer** → `try/finally`：

```go
// tsgo:internal/binder/binder.go:120-123
b := getBinder()
defer putBinder(b)
b.file = file
...
```

```csharp
var b = BinderPool.Get();
try {
    b.File = file;
    ...
} finally {
    BinderPool.Return(b);
}
```

**③ "立即求值、延迟调用"的 `defer f(...)()` 双括号形态** → `using var _ = ...`。这是 tsgo 高频形态（tracing 与作用域恢复）：

```go
// tsgo:internal/checker/checker.go:2188 —— tr.Push 立即执行并返回收尾闭包，defer 调该闭包
defer tr.Push(tracing.PhaseCheck, "checkSourceFile", map[string]any{...}, true)()

// tsgo:internal/collections/cow.go:49-52 —— EnterScope 返回恢复闭包
func (c *CopyOnWriteMap[K, V]) EnterScope() func() {
    saved := *c
    c.owned = false
    return func() { *c = saved }
}
```

```csharp
using var _ = tr.Push(TracingPhase.Check, "checkSourceFile", ...);  // Push 返回 IDisposable/Deferred

// EnterScope → IDisposable（主计划 §一.6 钦定）
public Scope EnterScope() { ... }               // Scope: 实现 Dispose 的 ref struct，见 §5.2
using var scope = map.EnterScope();
```

**④ 条件 defer / 循环内 defer** → 无法机械翻，手工重排并留 `// PORT-NOTE`。**特别警告**：Go 的 defer 在**函数**退出时运行，循环体里的 defer 会攒到函数尾；`using`/`finally` 在**块**退出时运行。凡遇循环内 defer，先确认 Go 语义到底要哪个（tsgo 中循环内 defer 极少，出现即是审计点）。

### 5.2 `Ts.Core.Deferred`

为 ③ 类提供统一载体（Ts.Compiler/Core/Deferred.cs，阶段 0 落地）：

```csharp
namespace Ts.Core;

/// 承接 Go 的 `defer fn()` 与 "返回恢复闭包" 惯用法。
/// ref struct：禁止逃逸到堆/异步边界，误用编译期报错（AOT 零分配）。
public readonly ref struct Deferred(Action onDispose) {
    public void Dispose() => onDispose();
}
```

判据：恢复逻辑是纯闭包 → `Deferred`；恢复逻辑带状态字段（如 CopyOnWriteMap 快照）→ 类型自带的具名 `Scope` ref struct（避免闭包分配）。两者都必须以 `using` 消费——**裸调用返回值直接丢弃会静默丢恢复逻辑**，在 `.editorconfig` 打开 CA2000/CS8073 类分析器兜底（07-aot-constraints.md）。

多个 defer 的 LIFO 次序 → 嵌套 `using`（声明序反向 Dispose，语义天然一致）。

---

## 6. 切片语义专章

### 6.1 全局约定：保留 null 语义 + null 容忍助手

Go 的 nil slice 是"合法的空"：`len(nil)==0`、`range nil` 零次循环、`append(nil, x)` 正常分配。C# 的 null 数组三连 NRE。**全局约定**：

1. **Go `[]T` → C# `T[]?` 或 `List<T>?`（可空注解），null 对应 nil**——不做"一律空数组化"的全局改写，因为 tsgo 大量 API 刻意区分 nil 与非 nil（如 `core.MapIndex` 对 nil 输入返回 nil，tsgo:internal/core/core.go:106-115；`Entries.Symlinks` 的 nil 表示"信息不可用"而非"无符号链接"，tsgo:internal/vfs/vfs.go:57-62）。null 语义**逐案**核对：调用点从不判 nil、仅 len/range → 可空转不可空 + 空数组初始化，留 `// PORT-NOTE`。
2. `len(s)` / `range s` / `append` 三大操作经 **null 容忍扩展方法**翻译，集中在 `Ts.Core.CollectionExtensions`：

```csharp
public static int Len<T>(this T[]? a) => a?.Length ?? 0;
public static int Len<T>(this List<T>? l) => l?.Count ?? 0;
public static T[] OrEmpty<T>(this T[]? a) => a ?? [];         // range 用：foreach (var x in xs.OrEmpty())
```

3. `[]*Node` → `Node[]`（主计划 §三/任务书钦定；元素本身是引用，Go 的指针切片与 C# 引用数组同构）。`NodeList.Nodes []*Node`（tsgo:internal/ast/ast.go:131-133）→ `public Node[] Nodes;`。
4. `[]byte` → `byte[]`；`[]string` → `string[]`；构建期集合 → `List<T>`（§6.3）。

### 6.2 SameMap 家族：“无变化返回原引用”必须保留

这是 transformer/checker 增量身份判定的承重语义（`UpdateExpressionStatement` 靠引用相等决定要不要新建节点，tsgo:internal/ast/ast_generated.go:1580-1584）。

```go
// tsgo:internal/core/core.go:151-165
func SameMap[T comparable](slice []T, f func(T) T) []T {
    for i, value := range slice {
        mapped := f(value)
        if mapped != value {
            result := make([]T, len(slice))
            copy(result, slice[:i])
            result[i] = mapped
            for j := i + 1; j < len(slice); j++ {
                result[j] = f(slice[j])
            }
            return result
        }
    }
    return slice   // 无变化：原引用原样返回
}
```

```csharp
// Ts.Compiler/Core/Core.cs —— 逐行同构；无变化时返回原数组引用
public static T[]? SameMap<T>(T[]? slice, Func<T, T> f) where T : IEquatable<T>? {
    if (slice is null) return null;                    // Go: range nil 零次循环后 return slice(nil)
    for (int i = 0; i < slice.Length; i++) {
        var value = slice[i];
        var mapped = f(value);
        if (!EqualityComparer<T>.Default.Equals(mapped, value)) {
            var result = new T[slice.Length];
            Array.Copy(slice, result, i);
            result[i] = mapped;
            for (int j = i + 1; j < slice.Length; j++) result[j] = f(slice[j]);
            return result;
        }
    }
    return slice;
}
```

要点：Go 约束 `T comparable` 且引用元素比较即指针比较 → C# `EqualityComparer<T>.Default` 对引用类型默认引用相等（未重写 Equals 时），对 `Node` 精确对应。**移植 `Node` 及全部 AST/Type 类不得重写 Equals/GetHashCode**（引用身份是语义，主计划 §一.6）。

`Same`（core.go:183-188）在 Go 里要比较 `&s1[0] == &s2[0]`（切片头不可直接比）；C# 数组本身是对象，**简化为 `ReferenceEquals(s1, s2)`**——行为一致且更强（Go 版对"同底不同长"返回 false，同样被 len 前置检查覆盖），留 `// PORT-NOTE: slice identity == array reference identity`。

### 6.3 append → List<T> / 数组助手

| Go 形态 | C# 移植 |
|---|---|
| 局部攒集 `var result []U; ... result = append(result, x); return result` | 判调用方是否依赖 nil：依赖 → `List<U>? result = null; (result ??= []).Add(x); return result?.ToArray();`；不依赖 → `List<U> result = []; ...; return result.ToArray();`（首选后者 + PORT-NOTE） |
| 字段上的 append（如 `w.fns = append(w.fns, fn)`，workgroup.go:63） | 字段直接改 `List<T>`：`fns.Add(fn)` |
| `append(dst, src...)` | `dst.AddRange(src)` |
| `slices.AppendSeq(make([]T,0,cap), seq)`（core.go:432） | `var list = new List<T>(cap); list.AddRange(seq);` |
| 预分配 `make([]T, n)` | `new T[n]` |
| `make([]T, 0, cap)` | `new List<T>(cap)` |
| 截断复用 `w.fns[end] = nil; w.fns = w.fns[:end]`（workgroup.go:83-85，置 nil 助 GC + 缩短） | `List<T>.RemoveAt(end)`（RemoveAt 自动清引用） |

**存储期与构建期分离**：长期存活的数据结构字段用 `T[]`（尺寸定格、省 List 头），构建过程用 `List<T>`，收尾 `ToArray()`。NodeList/工厂产出全走这个模式（02-ast-design.md 生成器落实）。

### 6.4 切片表达式 s[a:b] 的判据

Go 切片是零拷贝视图；C# 对应物按**用途**分流：

| 用途 | C# | 示例 |
|---|---|---|
| 只读、即用即弃（解析/扫描热路径） | `ReadOnlySpan<T>` / `AsSpan(a, b-a)` | `s.text[s.pos:]` 传给解码（scanner.go:450）→ `text.AsSpan(pos)`；字符串场景见 §8 |
| 需要存储/逃逸/跨 yield | 拷贝：`arr[a..b]`（Range 索引器分配新数组） | `slice[:i]` 存入结果 → `slice[..i]` |
| 传给既有 API 且切片会被继续切 | 传 (数组, offset, length) 三元或 `ArraySegment<T>` | 逐案 |

**共享底层数组的写穿语义**是审计点：Go 里 `sub := s[a:b]; sub[0] = x` 会改 s。C# 的 Span 同样写穿，数组拷贝则不会。凡 Go 代码对子切片**写入**，必须确认移植物保持（Span）或确认写穿不被依赖（拷贝 + PORT-NOTE）。tsgo 生产代码绝大多数子切片是只读消费，写穿点罕见。

`copy(dst, src)` → `Array.Copy(src, dst, n)` / `src.AsSpan(..n).CopyTo(dst)`。`slices.Clone` → `(T[])arr.Clone()` 或 `arr[..]`。`slices.Sort` → `Array.Sort`/`List.Sort`（**比较器语义见 §8.4，禁默认文化序**；`slices.SortStableFunc` → 需要稳定排序时**必须**用 LINQ `OrderBy`（稳定）或自研稳定排序，`Array.Sort` 是不稳定的内省排序——诊断排序等承重点专项核对，已增列 12-risks.md R22）。

---

## 7. map 语义专章

### 7.1 读缺失键 → 零值

Go 读缺失键得零值，双返回形态拿存在性；C# `Dictionary` 索引器对缺失键**抛异常**。约定：

| Go | C# |
|---|---|
| `v := m[k]`（利用零值） | `m.GetValueOrDefault(k)`（引用类型得 null，值类型得 default——与 Go 零值一致） |
| `v, ok := m[k]` | `m.TryGetValue(k, out var v)`（tsgo:internal/checker/checker.go:6253 `cached, ok := c.iterationTypesCache[key]` → `iterationTypesCache.TryGetValue(key, out var cached)`） |
| `_, ok := m[k]` | `m.ContainsKey(k)`（tsgo:internal/checker/checker.go:1323） |
| `m[k] = v` | `m[k] = v`（索引器写入=插入或覆盖，一致） |
| `delete(m, k)` | `m.Remove(k)`（缺失键静默，一致） |
| `len(m)` | `m.Count`（nil map 见 §7.2） |
| `clear(m)` | `m.Clear()` |
| `maps.Clone(m)`（cow.go:40） | `new Dictionary<K,V>(m)` |

**C# 索引器读 = Go 的隐藏地雷反转**：Go 程序员写 `m[k]` 从不担心缺失，直译成 C# 索引器会在缺失时抛 KeyNotFoundException。**规则：move 到 C# 后凡"读"一律 GetValueOrDefault/TryGetValue，索引器只用于写**。code review 检查项。

### 7.2 nil map：读安全、写 panic

Go 读 nil map 得零值、写 nil map panic。C# null 字典读写皆 NRE。tsgo 的标准形态是"懒初始化"：

```go
// tsgo:internal/collections/set.go:24-29
func (s *Set[T]) Add(key T) {
    if s.M == nil {
        s.M = make(map[T]struct{})
    }
    s.M[key] = struct{}{}
}
```

```csharp
public void Add(T key) {
    m ??= [];
    m[key] = default;      // 或 HashSet 直接 m.Add(key)，见 §7.5
}
```

约定：**map 字段默认可空 + 懒初始化形态照搬**（`??=` 一行对应 Go 三行）；只被无条件写入的 map 字段可在构造器初始化并声明为不可空（PORT-NOTE 标注）。**读 nil map**（Go 合法）在 C# 侧经 §7.1 的 GetValueOrDefault 仍会 NRE——凡字段可空且有读点，用 `m?.GetValueOrDefault(k)` 或先 null 判。

### 7.3 迭代序审计规则（承重，主计划 §一.6）

Go map 迭代序**随机**（每次运行都不同），这实质上是上游帮我们做过的 fuzzing：凡 tsgo 里 `for range m` 的结果影响输出，源码必然已经序无关或显式排序（证据：tsgo:internal/checker/checker.go:1052 `slices.Sorted(maps.Keys(typeofNEFacts))`——需要序时 Go 自己排）。C# `Dictionary` 的迭代序是**确定但无契约的近插入序**——它会让"悄悄依赖序"的移植 bug 在测试里全绿、在真实负载或版本升级时爆炸。

**铁律：每个 `for range map` 移植点必须带下列注释之一**（CI 用正则抽查 `foreach.*(Dictionary|Map|m\.Keys|m\.Values)` 与注释共存，08-testing-strategy.md）：

```csharp
// map-iter: 序无关 —— 结果聚合与顺序无关（如求和/求并/填另一张 map）
foreach (var (file, idx) in fileAssociations) { ... }

// map-iter: 已排序 —— Go 侧本就排序，此处照搬
foreach (var name in localizedMessages.Keys.Order(StringComparer.Ordinal)) { ... }
```

对应 Go 的显式排序惯用法翻译：`slices.Sorted(maps.Keys(m))` → `m.Keys.Order(StringComparer.Ordinal).ToArray()`（string 键）或 `m.Keys.Order().ToArray()`（数值键）。**禁止**无比较器的 `Order()` 用于 string（文化序陷阱，§8.4）。

> 建议（非门禁）：Debug 构建提供 `Ts.Core.ShuffledDictionary<K,V>` 包装，迭代时随机化顺序，在差分测试里复现 Go 的随机性以抓漏标注的点。是否落地由 08 定夺。

`collections.OrderedMap`（tsgo:internal/collections/ordered_map.go）是 tsgo 里**刻意有序**的 map，移植为自研 `OrderedMap<K,V>`（Dictionary + List 双结构，主计划 §一.6"原样移植"）——**不得**用 Dictionary 的意外插入序冒充。

### 7.4 迭代中修改

| 操作 | Go 语义 | .NET 语义 | 规则 |
|---|---|---|---|
| range 中 delete **当前 map** | 合法，已删的不再访问 | `Dictionary.Remove` 在 foreach 中**自 .NET Core 3.0 起合法**（不失效枚举器） | 直译。案例 tsgo:internal/project/checkerpool.go:466-468：`for file, idx := range p.fileAssociations { if idx == index { delete(p.fileAssociations, file) } }` → `foreach (var (file, idx) in fileAssociations) if (idx == index) fileAssociations.Remove(file);` |
| range 中 delete **另一 map** | 合法 | 合法 | 直译（tsgo:internal/checker/checker.go:16138-16139） |
| range 中**插入**当前 map | 合法但新条目是否被访问未定义 | `InvalidOperationException`！ | **不可直译**。先收集后插（buffer 到 List 再写），PORT-NOTE 标注；tsgo 中此形态罕见，出现即审计 |

### 7.5 键类型与相等语义

- **string 键**：Go 按字节相等/哈希；C# Dictionary 默认 comparer 对 string 是 ordinal——一致。**禁止**传入任何 CultureInfo 相关 comparer。大小写不敏感路径用 `StringComparer.OrdinalIgnoreCase` 前先核对 Go 侧算法（§8.5 EqualFold 差异）。
- **struct 键** → `readonly record struct`（主计划 §一.6）：编译器合成的成员逐字段相等，引用型字段默认引用相等 = Go 指针字段比较语义自动保真。示例（tsgo:internal/checker/checker.go:145-148、152-158）：

```go
type CachedTypeKey struct {
    kind   CachedTypeKind
    typeId TypeId
}
type NarrowedTypeKey struct {
    t            *Type
    candidate    *Type
    assumeTrue   bool
    checkDerived bool
}
```

```csharp
private readonly record struct CachedTypeKey(CachedTypeKind Kind, TypeId TypeId);
private readonly record struct NarrowedTypeKey(Type T, Type Candidate, bool AssumeTrue, bool CheckDerived);
```

- **指针键**（`map[*ast.Node]X`、LinkStore）→ `Dictionary<Node, X>(ReferenceEqualityComparer.Instance)`（主计划 §一.6）。**凡键是 AST/Type/Symbol 类，必须显式给 ReferenceEqualityComparer**——虽然这些类没重写 Equals 时默认就是引用相等，显式传入是为了(a)防未来某类意外获得值相等，(b)向读者声明语义。
- **`any` 参与的键**（`EnumLiteralKey{enumSymbol, value any}`，checker.go:102-106）：Go 对 interface 键按动态类型+值比较；C# `object` 字段经 `EqualityComparer<object>.Default` 走虚 `Equals` ——对装箱 string/double 同为值相等，基本对齐。**NaN 例外**：Go 的 float NaN 键因 `NaN != NaN` 永远查不中（tsgo:internal/checker/checker.go:25277-25279 的注释与 `enumNaNLiteralTypes` 旁路正因此存在）；而 C# `EqualityComparer<double>.Default` 认为 NaN 等于 NaN、装箱 `double.NaN.Equals(double.NaN)` 也为 true——**行为相反**。规则：照搬 Go 的 NaN 旁路代码（NaN 根本不进主 map，两边行为于是一致），并在该处 PORT-NOTE 记录这一反向差异，登记 12-risks.md。
- `map[T]struct{}` 集合惯用法 → tsgo 已封装为 `collections.Set[T]`（set.go:5），C# 移植为包装 `HashSet<T>` 的同名 `Set<T>`（保 nil 接收者 `Has` 的 null 容忍语义——`Has` 是 §10.3 的扩展方法候选）。
- `collections.SyncMap`（syncmap.go:8，泛型化 sync.Map）→ `ConcurrentDictionary<K,V>`：`Load`→`TryGetValue`、`Store`→索引器写、`LoadOrStore`→`GetOrAdd`（注意 GetOrAdd 的工厂可能并发执行多次、只入选一个——与 sync.Map 语义一致）、`Range`→枚举（快照不一致性两边等价，序规则同 §7.3）。

---

## 8. 字符串专章

### 8.1 世界观：UTF-16 原生

主计划已裁定（§一.6）：**删除 PositionMap 与 rune 位置机制，一切位置即 UTF-16 code unit**。这改变了字符串各操作的翻译目标：

| Go（UTF-8 世界） | C#（UTF-16 世界） | 说明 |
|---|---|---|
| `s[i]` → **byte** | `s[i]` → **char**（UTF-16 单元） | ASCII 范围（<0x80）两边同值，tsgo 热路径全是 ASCII fast path，语义自动保持 |
| `len(s)` = 字节数 | `s.Length` = UTF-16 单元数 | **所有存量位置算术随世界观整体平移**，不做逐点换算——这正是删 PositionMap 的意义 |
| `utf8.RuneSelf`（0x80）判 ASCII | `< 0x80` | `b < utf8.RuneSelf` → `c < 0x80` |
| `utf8.DecodeRuneInString(s[pos:])` | `Rune.DecodeFromUtf16(s.AsSpan(pos), out var rune, out var size)` | size 从字节数变 UTF-16 单元数（1 或 2），下游 `pos += size` 语义自动正确 |
| 多字节 UTF-8 序列处理 | 代理对处理 | 无效序列：Go 得 `utf8.RuneError`(U+FFFD)/size=1；`Rune.DecodeFromUtf16` 对孤代理返回 `OperationStatus` 非 Done——包一层助手统一产出 U+FFFD/size=1 对齐 Go |

**范例：Scanner.charAndSize**（tsgo:internal/scanner/scanner.go:441-451）：

```go
func (s *Scanner) charAndSize() (rune, int) {
    // Fast path: a single ASCII byte. ...
    if s.pos < s.end {
        if b := s.text[s.pos]; b < utf8.RuneSelf {
            return rune(b), 1
        }
    }
    return utf8.DecodeRuneInString(s.text[s.pos:])
}
```

```csharp
private (int Ch, int Size) CharAndSize() {
    // Fast path: a single BMP char below 0x80.（PORT-NOTE: byte→UTF-16 unit，位置语义整体平移）
    if (pos < end) {
        char c = text[pos];
        if (c < 0x80) return (c, 1);
    }
    return StringUtil.DecodeRuneAt(text, pos);   // 统一助手：孤代理→(0xFFFD,1)
}
```

### 8.2 range string 按 rune → EnumerateRunes

```go
// tsgo:internal/jsnum/string.go:66-70 —— range string 逐码点解码
for _, r := range s {
    if !isNumberRune(r) {
        return NaN()
    }
}
```

```csharp
foreach (var r in s.EnumerateRunes()) {
    if (!IsNumberRune(r.Value)) return Number.NaN;
}
```

带索引形态 `for i, r := range s`：Go 的 i 是**字节**偏移 → C# 用手动循环拿 UTF-16 偏移（世界观平移）：

```csharp
for (int i = 0; i < s.Length; ) {
    var (ch, size) = StringUtil.DecodeRuneAt(s, i);
    ...            // i 即 UTF-16 偏移，对应 Go 的字节偏移职责
    i += size;
}
```

`core.UTF16Len`（tsgo:internal/core/core.go:485-500，UTF-8→UTF-16 长度换算）之类**换算函数整体蒸发**：`Utf16Len(s)` → `s.Length`。反向的两个基架垫片（errors.txt 波浪线按码点计数、sourcemap 续行标记按 `Encoding.UTF8.GetByteCount`）属测试基架，归 08-testing-strategy.md。

`[]rune(s)` → `s.EnumerateRunes().ToArray()`（罕见，逐案）；`string(r)` → `rune.ToString()` 或 `char.ConvertFromUtf32(cp)`；`utf16.RuneLen(r)` → `rune.Utf16SequenceLength`。

### 8.3 strings.Builder → StringBuilder

```go
// tsgo:internal/checker/checker.go:28919
var sb strings.Builder
sb.WriteString(prefix)
sb.WriteRune(ch)
sb.WriteByte('.')
s := sb.String()
```

```csharp
var sb = new StringBuilder();
sb.Append(prefix);
sb.AppendRune(ch);      // 自研扩展：Span<char> buf + rune.EncodeToUtf16(buf) + sb.Append(buf)
sb.Append('.');
var s = sb.ToString();
```

`WriteByte` 仅在 Go 侧写 ASCII 时合法直译为 `Append(char)`；若 Go 在拼**非 ASCII 字节**（手工组 UTF-8），那是输出字节层代码，移植目标改为 `byte[]`/`IBufferWriter<byte>`（printer 输出层判据，06 逐包标注）。池化的 `singleLineStringWriterPool`（tsgo:internal/printer/singlelinestringwriter.go:13）→ ObjectPool（§11）。

### 8.4 比较与排序：全部 Ordinal，手写比较器逐行移植

`InvariantGlobalization=true`（主计划 §三）已消灭文化表加载，但 **Invariant culture 排序 ≠ Ordinal 排序**，仍须显式：

| Go | C# | 备注 |
|---|---|---|
| `a == b` / map 键相等 | `==` / 默认 comparer | 皆 ordinal，安全 |
| `a < b`、`strings.Compare` | `string.CompareOrdinal(a, b)` | ⚠ 见下方非 BMP 警告 |
| `sort` 中的 string 序 | `StringComparer.Ordinal` | **永不**裸 `Order()`/`CompareTo` |
| `strings.EqualFold`（tsgo:internal/stringutil/compare.go:12） | ❌ 不等于 `OrdinalIgnoreCase`！ | Go 是 Unicode simple case folding，OrdinalIgnoreCase 是逐单元 ToUpperInvariant 比较，边缘码点有分歧。**移植 compare.go 的逐 rune 算法**，不用 BCL |
| `CompareStringsCaseInsensitive`（compare.go:34-60，逐 rune `unicode.ToLower` 比较） | 逐行移植：`Rune.ToLowerInvariant` 逐码点 | 同上 |
| `unicode.ToLower/ToUpper` | `Rune.ToLowerInvariant/ToUpperInvariant` | Unicode 表版本差异是理论风险，登记 12-risks.md |

**非 BMP 警告（审计点）**：Go 的 string 序 = UTF-8 字节序 = **码点序**；C# `CompareOrdinal` = UTF-16 单元序，在 [U+E000, U+FFFF] 与增补平面字符共存时与码点序**不同**（0xE000-0xFFFF 的单元值大于代理区 0xD800-0xDFFF）。规则：**输出承重的排序**（诊断排序、symbol 名排序、keys 排序进基线）若可含非 BMP 字符，用自研 `StringUtil.CompareRuneOrdinal`（逐码点比较，= Go 语义）；纯内部一致性排序用 CompareOrdinal 即可。逐点判定记入移植时的 PORT-NOTE，风险册挂总项。

**tspath.toFileNameLowerCase**（tsgo:internal/tspath/path.go:645-685）是"手写比较器逐行移植"的典型：它**故意**只降 A-Z 并特判土耳其 İ（U+0130），绝不能用 `ToLowerInvariant()` 替换——上游为路径规范化的跨平台稳定性专门写的算法，照抄（其 unsafe.String 部分见 §15）。

### 8.5 格式化

| Go | C# | 备注 |
|---|---|---|
| `fmt.Sprintf("%s/%d", a, b)` | `$"{a}/{b}"` | InvariantGlobalization 下插值安全；仍禁止对 double 直接插值（走 jsnum 格式层，主计划阶段 1 门禁） |
| `fmt.Sprintf("%v", x)` | 逐案：x 的 Go 打印形态是什么就复刻什么 | `%v` 对 bool 是 `true/false`、对 nil 是 `<nil>`——凡进诊断/基线必须核对 |
| `%T`（动态类型名） | `x.GetType().Name` | 仅用于 panic 消息（§4.3），文本不承重 |
| `%q`（带引号转义） | **不移植**（裁决 R9；已 grep 全仓核实：生产代码中 `%q` 仅见于 panic 消息、日志/协议错误文本（lspwatcher、jsonrpc、api）、生成器与 vfstest 等测试/调试路径，无一进入 conformance 基线）。需要处引用 StringUtil 的 JS 字符串引号化 | 逐点 PORT-NOTE |
| `strconv.Itoa(n)` | `n.ToString()`（int 无文化差异风险，安全） | — |
| `strconv.FormatFloat` / 数字→字符串 | **一律经 `JsNum.Number` 的 ES6 格式层**（主计划阶段 1 门禁钉死） | 禁 `double.ToString()` 裸用 |
| `strings.HasPrefix/HasSuffix/Contains` | `StartsWith/EndsWith/Contains` + `StringComparison.Ordinal` | ⚠ `StartsWith(string)` 无比较器重载是**文化敏感**的——必须显式 Ordinal；`char` 重载安全 |
| `strings.Index/LastIndex` | `IndexOf/LastIndexOf(..., StringComparison.Ordinal)` | 返回值：两边都是 -1 表缺失；但 Go 返回**字节**偏移→世界观平移为 UTF-16 偏移，自动一致 |
| `strings.Split/Join/TrimPrefix/CutPrefix/ReplaceAll` | `Split/string.Join/`自研 `TrimPrefix`/`CutPrefix`/`Replace` | `CutPrefix` 返回 (rest, ok)（jsnum/string.go:77）→ Try 模式或元组 |
| `strings.TrimFunc(s, pred)` | 自研（BCL Trim 只收 char 集合，pred 按 rune） | jsnum/string.go:55 |

---

## 9. struct 值语义 vs class

### 9.1 保持 C# struct 的类型（白名单制）

**默认规则：Go struct → C# class**（tsgo 中绝大多数 struct 经指针使用，身份语义）。仅下列几类保持/转为 struct：

| 类别 | 判据 | 案例 |
|---|---|---|
| 小型不可变值 | ≤16 字节、无身份语义、按值到处传 | `TextRange`（tsgo:internal/core/text.go:9-12，8 字节）→ `readonly record struct TextRange(int Pos, int End)`；`TextPos` 消融为 int |
| 缓存键 | 用作 Dictionary 键的 Go 可比较 struct | `CacheHashKey`（checker.go:17313，`xxh3.Uint128` 包装）→ `readonly record struct CacheHashKey(UInt128 Value)`；§7.5 全部键 struct |
| 语义数值包装 | 带方法的数值命名类型 | `jsnum.Number`（jsnum.go:20）→ `readonly record struct Number(double Value)` + 运算符；`Version`（semver）逐案 |
| 帧/枚举对 | 多返回值元组化目标 | `(Rune, int)` 等即用即弃组合 |

**判 struct 的红线**：一旦类型有 (a) 可变字段被共享修改，(b) 被 map 以引用身份索引，(c) 参与继承——就必须 class。`NodeList`（ast.go:131）虽小但以 `*NodeList` 身份共享 → **class**。

### 9.2 赋值拷贝语义差异警告

Go struct 赋值 = 浅拷贝；C# class 赋值 = 引用别名。两个高危惯用法：

**① 快照-恢复 `saved := *p; ...; *p = saved`**（tsgo:internal/collections/cow.go:50-52）：

```go
func (c *CopyOnWriteMap[K, V]) EnterScope() func() {
    saved := *c            // 值快照
    c.owned = false
    return func() { *c = saved }   // 整体回写
}
```

C# class 上没有 `*c = saved`。规则：此惯用法的载体类型加显式 `Snapshot()/Restore(in snapshot)` 成员（内部逐字段拷贝），或将该类型做成 struct 且仅以字段内嵌（禁独立传递）。CopyOnWriteMap 移植取后者不可行（有泛型方法集与逃逸用法）——取前者：

```csharp
public sealed class CopyOnWriteMap<K, V> where K : notnull {
    private Dictionary<K, V>? m;
    private bool owned;
    public readonly record struct Snapshot(Dictionary<K, V>? M, bool Owned);
    public Scope EnterScope() { var saved = new Snapshot(m, owned); owned = false; return new Scope(this, saved); }
    public readonly ref struct Scope(CopyOnWriteMap<K, V> map, Snapshot saved) {
        public void Dispose() { map.m = saved.M; map.owned = saved.Owned; }
    }
}
```

**② 归零复用 `*b = Binder{bindFunc: b.bindFunc}`**（tsgo:internal/binder/binder.go:117）：class 上写显式 `Reset()`，逐字段回零（**字段清单与 struct 定义同步维护**——新增字段忘进 Reset 是经典漏洞，生成器可产出 Reset 的包先生成，其余手写 + review checklist）。

**③ 循环变量/参数是 struct 时的隐式拷贝**：Go `for _, t := range ranges`（t 是拷贝，改 t 不影响元素）——C# struct foreach 变量同为拷贝且**只读**，直译安全；但若 Go 代码利用"拷贝后修改再存回"，C# 端注意 `ranges[i] = t` 的写回不能漏。

### 9.3 零值可用性 → C# 默认构造策略

Go 类型零值即合法状态是普遍契约（cow.go:9 "The zero value is an empty map ready to use"；`Set` 零值可 Has）。C# 对策分层：

| 场景 | 策略 |
|---|---|
| struct 移植物 | 保证 `default(T)` 行为 = Go 零值行为（TextRange 的 default 是 {0,0}，Go 同；"未定义"另有 `UndefinedTextRange()`=(-1,-1)（text.go:18）→ `static readonly TextRange Undefined = new(-1, -1);`） |
| class 移植物、零值可用被依赖 | 字段初始化器给出等价零值状态；无参构造保持公开 |
| class 移植物、Go 中必经构造函数（NewXxx） | 构造函数对应 `NewXxx` → C# 构造器或静态工厂（保名 `Scanner.NewScanner`？**裁定**：Go `NewT(...)` → C# 构造器 `new T(...)`；Go 同包多个构造变体 `NewTWithX` → 静态工厂方法原名） |
| `var x T` 后逐字段填充 | `var x = new T();` + 对象初始化器 |

### 9.4 embedding 的内存内联不再存在

Go 值内嵌（`Identifier struct { PrimaryExpressionBase; FlowNodeBase; Text string }`，tsgo:internal/ast/ast_generated.go:620-624）把字段内联进同一分配；C# 用继承+扁平化字段达成同等布局（02-ast-design.md）。**不要**把数据 mixin 翻成"struct 字段嵌套"——那会引入 C# 可变 struct 字段的取址/拷贝雷区。见 §13 决策树。

---

## 10. 接口与 nil

### 10.1 typed-nil 陷阱在 C# 消失

Go 的接口值 = (类型, 指针) 对：存了 `(*T)(nil)` 的接口 `!= nil`。tsgo 被迫为此立规（tsgo:internal/ast/ast.go:176-177 注释："Interface values stored in AST nodes are never typed nil values. Construction code must ensure..."）。C# 引用就是引用，**这一整类约束与 bug 消失**：相关防御性注释在移植时删除，替换为一行 `// PORT-NOTE: Go typed-nil constraint not applicable in C#`（只在原注释显眼处如 ast.go:176 保留一处说明，其余静默删）。

审计义务反转：唯一要留神的是 Go 代码**故意利用** typed-nil 区分"无值"与"有类型的空值"——tsgo 遵守上述纪律，理论上不存在；发现即为上游 bug，报 issue 并按语义移植。

### 10.2 接口断言 → 模式匹配

| Go | C# |
|---|---|
| `v, ok := x.(T)` | `x is T v`（ok 分支即 pattern 命中） |
| `if _, ok := x.(T); ok` | `x is T` |
| `x.(T)`（失败 panic） | `(T)x`（失败 InvalidCastException，§4.3） |
| `switch x.(type)` | `switch` 类型模式（§14） |

示例（tsgo:internal/debug/debug.go:36-38）：

```go
if m, ok := member.(interface{ KindString() string }); ok {
    detail = m.KindString()
}
```

C# 没有结构化接口——匿名接口断言按目标成员改为具名接口/基类检查：`if (member is Node n) detail = n.KindString();`（逐案，PORT-NOTE）。

### 10.3 nil 接收者方法 → 扩展方法（主计划 §一.1 钦定）

判据：**方法体以接收者判 nil 开头**（`if n == nil` / `if s == nil`）即为 nil 容忍方法，C# 实例方法对 null this 会 NRE，必须译为扩展方法：

```go
// tsgo:internal/ast/ast.go:2215-2218
func (node *ImportAttributesNode) GetResolutionModeOverride() (core.ResolutionMode, bool) {
    if node == nil {
        return core.ResolutionModeNone, false
    }
    ...
}

// tsgo:internal/collections/set.go:16-21
func (s *Set[T]) Has(key T) bool {
    if s == nil {
        return false
    }
    _, ok := s.M[key]
    return ok
}
```

```csharp
// Ast/Extensions.cs
public static bool TryGetResolutionModeOverride(this ImportAttributesNode? node, out ResolutionMode mode) {
    if (node is null) { mode = ResolutionMode.None; return false; }
    ...
}

// Collections/Set.cs
public static bool Has<T>(this Set<T>? s, T key) => s is not null && s.set.Contains(key);
```

调用形态 `node.GetResolutionModeOverride()` 原样保留（扩展方法接受 null this），diff 対照零成本。**生成器规则**（02/04）：对生成面自动检测此形态；手写面靠本节 + review checklist。

---

## 11. 并发映射总表

每行给一个真实出处示例。总原则：**编译管线内的并行**（parse/bind/check）一律走 `WorkGroup` 抽象跑在 `LargeStackExecutor` 的 64MB 大栈线程上（主计划 §一.5，01-architecture.md）；**服务层 IO 并发**（LSP/watch/API）用 Task/Channel。

| Go | C# | 真实示例（Go → C#） |
|---|---|---|
| `go func(){...}()`（服务层） | `Task.Run` | tsgo:internal/lsp/server.go:439 `go func() { readLoopErr <- s.readLoop(ctx) }()` → `_ = Task.Run(async () => await readLoopErr.Writer.WriteAsync(await ReadLoopAsync(ct)));` |
| `go`（编译管线扇出） | `WorkGroup.Queue`（内部为大栈线程池） | tsgo:internal/core/workgroup.go:34-41 `w.wg.Go(fn)` → `Queue` 实现里 `tasks.Add(LargeStackExecutor.Run(fn))` |
| `make(chan T, n)` / `make(chan T)` | `Channel.CreateBounded<T>(n)` / `CreateUnbounded<T>()`（System.Threading.Channels，主计划 §一.5） | tsgo:internal/lsp/server.go:611 `responseChan := make(chan *lsproto.ResponseMessage, 1)` → `Channel.CreateBounded<ResponseMessage>(1)` |
| `ch <- v` / `<-ch` | `await writer.WriteAsync(v)` / `await reader.ReadAsync(ct)` | 同上调用点 |
| `close(ch)` + 广播空 struct | `writer.Complete()`；`chan struct{}` 关闭广播 → `TaskCompletionSource`/`CancellationTokenSource` | tsgo:internal/lsp/server.go:73 `initComplete: make(chan struct{})` 关闭即广播 → `TaskCompletionSource initComplete`（`InitComplete` 属性暴露 `Task`） |
| `select { case <-ctx.Done(): ...; case v := <-ch: ... }` | `await reader.ReadAsync(ct)`（取消即 OperationCanceledException 分支）或 `Task.WhenAny` | tsgo:internal/lsp/server.go:629-633 → `var resp = await responseChan.Reader.ReadAsync(ct);` + catch OCE |
| `errgroup.Group` | `Task.WhenAll` + 首异常传播（WhenAll 聚合 AggregateException，取 First 对应 errgroup"首错"语义，PORT-NOTE） | tsgo:internal/core/workgroup.go:99 |
| `ThrottleGroup`（errgroup+semaphore，workgroup.go:92-110） | `SemaphoreSlim` + `Task.WhenAll`：Go 侧"入组即起协程、组内抢信号量"，保结构直译 | — |
| `sync.WaitGroup` | 编译管线：WorkGroup.RunAndWait（内部 `Task.WaitAll`）；一般场景 `CountdownEvent` | tsgo:internal/core/bfs.go:96 `var wg sync.WaitGroup` → `using var wg = new CountdownEvent(1);`（AddCount/Signal/Wait） |
| `sync.Once` | `Lazy<T>` / 无值时 `Once` 自研小类型（`Interlocked.CompareExchange` 位） | tsgo:internal/checker/checker.go:882 `emitResolverOnce sync.Once` + do → `emitResolver ??=` 于 `LazyInitializer.EnsureInitialized(ref emitResolver, ref emitResolverLock, ...)` |
| `sync.OnceValue(f)` | `static readonly Lazy<T>`（LazyThreadSafetyMode.ExecutionAndPublication） | tsgo:internal/core/nodemodules.go:75 `NodeCoreModules = sync.OnceValue(func() map[string]bool {...})` → `static readonly Lazy<Dictionary<string, bool>> NodeCoreModules = new(() => ...);`（调用点 `NodeCoreModules()` → `NodeCoreModules.Value`） |
| `sync.Pool` | 一律 `Ts.Core.ObjectPool<T>`（01-architecture.md §5.2 自研：ConcurrentQueue + 保留上限 + `Rental`；不引入 Microsoft.Extensions.ObjectPool——裁决 R8） | tsgo:internal/binder/binder.go:104-118 `binderPool` + `putBinder` 归零 → `ObjectPool<Binder>` + `Binder.Reset()`（§9.2②）；同型 tsgo:internal/printer/emitcontext.go:51 |
| `atomic.Bool` | `Volatile.Read/Write(ref bool)` 或 `int` + Interlocked | tsgo:internal/core/workgroup.go:28 `done atomic.Bool` → `private volatile bool done;`（Load/Store 即读写） |
| `atomic.Int64/Uint32.Add` | `Interlocked.Increment/Add(ref long/uint)` | tsgo:internal/checker/checker.go:578 `nextCheckerID atomic.Uint32` → `Interlocked.Increment(ref nextCheckerId)`；tsgo:internal/printer/emitcontext.go:427 同 |
| `atomic.Uint64.CompareAndSwap` | `Interlocked.CompareExchange(ref ulong, ...)` | ast.go:183 `Node.id` 惰性分配（主计划 §一.1 钦定此译法） |
| `atomic.Pointer[T]` | `Volatile.Read/Write(ref T)` + `Interlocked.CompareExchange<T>` | — |
| `sync.Mutex` | `System.Threading.Lock`（主计划 §一.5）；`lock(x){}` 语句消费 | tsgo:internal/compiler/checkerpool.go:53 `locks []*sync.Mutex`（每 checker 一把）→ `Lock[] locks` |
| `sync.RWMutex` | `ReaderWriterLockSlim`（主计划 §一.5）；单字段惰性缓存的 RLock-升级形态可简化为 Volatile 双检 + Lock，PORT-NOTE | tsgo:internal/ast/ast.go:2701-2715 `ECMALineMap` 双检 → `Volatile.Read(ref ecmaLineMap)` 双检 + `lock` |
| `sync.Map` | `ConcurrentDictionary<K,V>`（§7.5 SyncMap 行） | tsgo:internal/collections/syncmap.go |
| `chan struct{}` 作信号量 | `SemaphoreSlim(n, n)` | tsgo:internal/project/checkerpool.go:74 `diagSem chan struct{}` → `SemaphoreSlim diagSem`；tsgo:internal/core/semaphore.go 整包 → SemaphoreSlim 包装 |
| `context.Context` | 显式 `RequestContext`（01-architecture.md §4.4 全仓总则：取消/requestID/CheckerLifetime 三值的不可变小对象）。**池边界签名规则：Go 首参 `ctx` → C# 首参 `RequestContext`**；仅消费取消位的深层函数可降为 `CancellationToken`，逐案（裁决 R5） | tsgo:internal/checker/checker.go:2182 `checkSourceFile(ctx context.Context, ...)` → `CheckSourceFile(RequestContext ctx, ...)`；`c.ctx.Err() != nil`（tsgo:internal/checker/utilities.go:1636-1641）→ `ctx.Cancellation.IsCancellationRequested` |
| `context.WithCancel/WithTimeout` | `CancellationTokenSource` / `CTS.CancelAfter` | — |
| context 携带值（requestID 等） | `RequestContext` 成员显式流动（`RequestId`/`CheckerLifetime`）；`AsyncLocal<T>` 为**已否决备选**（01 §4.4 的理由：AOT 下可用但不透明、难审计）（裁决 R5） | tsgo:internal/project 的 requestID → `RequestContext.RequestId`，06 逐点登记 |
| `time.Timer` / `time.AfterFunc` | `System.Threading.Timer` / `ITimer`（可测试性用抽象，08） | tsgo:internal/project/checkerpool.go:66 `cleanupTimer` |
| goroutine 泄漏防护（defer close） | `try/finally` + `Complete()`/`Cancel()` | 逐案 |

**确定性义务**：主计划 §四.3 要求单线程与 4-checker 并行输出全等。翻译并发代码时不得引入"结果依赖完成序"的聚合——凡 Go 侧按 index 归位（结果槽数组），C# 照搬结果槽模式，禁用 `ConcurrentBag` 等无序聚合。

---

## 12. iter.Seq / yield

Go 1.23 迭代器 `iter.Seq[T] = func(yield func(T) bool)` 是**push 模型**；C# `IEnumerable<T>`+`yield return` 是**pull 模型**。消费端等价，生产端翻译分三型：

**① 生成器函数（yield 循环体自含）→ 迭代器方法**（最常见，机械翻译）：

```go
// tsgo:internal/core/core.go:435-455（节选）
func ComputeECMALineStartsSeq(text string) iter.Seq[TextPos] {
    return func(yield func(TextPos) bool) {
        ...
        if !yield(lineStart) {
            return
        }
        ...
    }
}
```

```csharp
public static IEnumerable<int> ComputeEcmaLineStartsSeq(string text) {
    ...
    yield return lineStart;   // 消费者 break 即对应 Go 的 yield 返回 false
    ...
}
```

`yield(x) == false` 的提前终止分支在 C# 中消失（运行时接管），相关 `if !yield(...) { return }` 塌缩为一行 `yield return`。

**② push→pull 适配器（把回调 API 包成 Seq）**——C# 的 yield 不能在 lambda 里用，不可机械翻：

```go
// tsgo:internal/ast/ast.go:195-206 —— 把 ForEachChild 回调包成迭代器，并做 true/false 语义反转
func (n *Node) IterChildren() iter.Seq[*Node] {
    return func(yield func(*Node) bool) {
        n.ForEachChild(func(child *Node) bool {
            return !yield(child)
        })
    }
}
```

判据（按优先序）：
1. **调用点回调化**：消费点不真需要 pull 语义（只是 for-range 全遍历）→ 直接改用 `ForEachChild(child => {...})`，删中间层；
2. **生成 per-kind 迭代器**：需要 pull（中途 break、zip 等）且在热路径 → 由生成器为每个节点类型产出 `IEnumerable<Node> Children()`（逐字段 yield return，登记 04-codegen.md 候选清单）；
3. **小缓冲**：冷路径 → `List<Node> buf = []; ForEachChild(c => { buf.Add(c); return false; }); return buf;`，PORT-NOTE 标注分配成本。

Go 侧 true/false 语义反转注释（"true stops a TS visitor early, whereas false stops a Go iterator yield"）随形态 1/2 消失。

**③ Seq 组合子** → 静态泛型方法 + yield（不引入 LINQ 链在热路径；LINQ 本身 AOT 安全，允许用于冷路径）：`core.FilterSeq/ConcatenateSeq/Enumerate`（tsgo:internal/core/core.go:52,698,714）→ `Core.FilterSeq(...)`（内部 `foreach + if + yield return`）、`Concat`、`Core.Enumerate` → `seq.Index()`（.NET 9 内建）。

`iter.Seq2[K,V]` → `IEnumerable<(K key, V value)>`（tsgo:internal/vfs/vfstest/vfstest.go:645）或既有 `IEnumerable<KeyValuePair<K,V>>`（map 包装类型上保 KVP 省转换）。`for x := range seq` → `foreach`。`slices.Collect(seq)` → `seq.ToArray()`；`maps.Keys(m)`（返回 Seq）→ `m.Keys`。

---

## 13. 嵌入 → 继承 or 接口的决策树

完整层次论证见 02-ast-design.md（AST）与 03-checker-design.md（Type）。翻译现场按此树判定：

```
Go: type X struct { E; ... }（匿名嵌入 E）
│
├─ X 是 AST 节点（ast.json 管辖）？
│   ├─ E 是 Node 系 base（18 个单继承链成员）→ C# 基类：取 extends 中最派生者（02 规则1）
│   ├─ E 是 13 数据 mixin 之一 → 字段扁平化到 X + X 实现同名接口（不加 I）+ Node 虚访问器 override（02 规则2）
│   └─ E 是 4 无字段 brand base → X 实现标记接口（02 规则3）
│
├─ X 是 Checker Type 系（types.go 24 struct）？
│   └─ 严格单嵌入树 → 直接 C# 单继承（03；ObjectType/TypeReference/InterfaceType 不得 abstract）
│
├─ E 是接口（接口嵌入接口）？
│   └─ C# 接口继承：interface IX : IE（tsgo:internal/lsp/lsproto/lsp.go:70-72 TextDocumentPositionParams 嵌入 HasTextDocumentURI → `interface : IHasTextDocumentUri`）
│
└─ 其他普通 struct 嵌入（少量散例）：
    ├─ X 语义上 IS-A E，且 E 的方法集被外部以 X 调用 → 继承（E 若为 struct 需升 class）
    ├─ 仅为字段/方法转发便利（HAS-A）→ 组合：具名私有字段 + 手写转发成员（C# 无嵌入促升，转发显式写出）
    └─ E 是 Mutex/Once 等工具型 → 永远组合（具名字段）
```

判 IS-A/HAS-A 的操作性标准：grep `X` 的使用点，若存在把 `*X` 赋给 `*E` 形参/字段、或以 `x.EMethod()` 依赖 E 的多态——IS-A；只有 `x.e.Method()` 式内部使用——HAS-A。注意 Go 值内嵌没有多态（无虚方法），"IS-A + 需要 override"只出现在接口嵌入与 AST/Type 两大体系里，其余散例几乎全是 HAS-A 组合。

具名字段（如 `SyncMap` 的 `m sync.Map`，syncmap.go:11；`CopyOnWriteSet` 的 `m CopyOnWriteMap[...]`，cow.go:55-57）**不是嵌入**，照搬为私有字段即可。

---

## 14. 类型 switch → switch 模式匹配

```go
// tsgo:internal/checker/checker.go:25271-25291
func (c *Checker) getEnumLiteralType(value any, enumSymbol *ast.Symbol, symbol *ast.Symbol) *Type {
    var flags TypeFlags
    switch v := value.(type) {
    case string:
        flags = TypeFlagsEnumLiteral | TypeFlagsStringLiteral
    case jsnum.Number:
        flags = TypeFlagsEnumLiteral | TypeFlagsNumberLiteral
        if v.IsNaN() { ... }
    default:
        panic("Unhandled case in getEnumLiteralType")
    }
    ...
}
```

```csharp
private Type GetEnumLiteralType(object value, Symbol enumSymbol, Symbol symbol) {
    TypeFlags flags;
    switch (value) {
        case string:
            flags = TypeFlags.EnumLiteral | TypeFlags.StringLiteral;
            break;
        case Number v:
            flags = TypeFlags.EnumLiteral | TypeFlags.NumberLiteral;
            if (v.IsNaN) { ... }
            break;
        default:
            throw new InvalidOperationException("Unhandled case in getEnumLiteralType");
    }
    ...
}
```

对照规则：

| Go | C# |
|---|---|
| `switch v := x.(type)` | `switch (x)` + 类型模式 `case T v:`（不用绑定名时省 `v`） |
| `case nil:` | `case null:` |
| `case A, B:`（多类型一臂，v 为原接口型） | `case A or B:`（**无绑定**；若臂内要用值，Go 侧此时也只能用接口方法——C# 改为 `case A or B:` 后按需再 is-cast，PORT-NOTE） |
| 无 default（静默穿落） | 同样省 default（保空语义） |
| `default: panic(...)` | `default: throw ...`；穷尽已知类型时可用 `switch` 表达式 + `_ => throw` |
| 值 switch `switch x { case a, b: }` | `switch (x) { case a: case b: }` 或 `case a or b:` |
| `switch { case cond1: ... }`（无表达式条件链，如 scanner.go:1094-1110） | `switch { ... }` 无法直译 → if/else-if 链**保持臂序**（首匹配语义承重） |
| `fallthrough` | C# 禁隐式穿落 → `goto case ...` 或复制语句，PORT-NOTE（tsgo:internal/core/core.go:445-449 的 `\r\n` fallthrough 属此类） |

interface 值上做业务多态的大型 type switch（`evaluator.go:143`、`nodebuilderimpl.go:938`）机械照翻；**不要**"顺手"重构为虚方法——保持与 Go 的分支排布 diff 对照。

---

## 15. unsafe.String / 零拷贝 → string.Create / Span

tsgo 的 unsafe 使用全部是**构串零拷贝**优化（避免 `string(b)` 的第二次拷贝），共 5 处（vfstest/内部各 1、tspath 1、fswatch FFI 2——后者随 fswatch 重写消失）。

```go
// tsgo:internal/tspath/path.go:653-676（toFileNameLowerCase 快路径，节选）
b := make([]byte, len(fileName))
for i := 0; i < len(fileName); i++ {
    c := fileName[i]
    if 'A' <= c && c <= 'Z' {
        c += 'a' - 'A' // +32
    }
    b[i] = c
}
// SAFETY: We construct a string that aliases b's backing array without copying. ...
return unsafe.String(&b[0], len(b))
```

```csharp
// C# 官方等价物：string.Create —— 在最终字符串的内存里就地写入，零多余拷贝且完全安全
return string.Create(fileName.Length, fileName, static (span, src) => {
    for (int i = 0; i < src.Length; i++) {
        char c = src[i];
        if (c is >= 'A' and <= 'Z') c += (char)32;
        span[i] = c;
    }
});
```

规则：

| Go 形态 | C# |
|---|---|
| `unsafe.String(&b[0], len(b))`（[]byte→string 零拷贝） | `string.Create(len, state, spanAction)`；Go 的 SAFETY 注释四条款（生命周期/初始化/不可变/非空）全部由 string.Create 结构性保证 → 注释缩为一行 PORT-NOTE |
| `unsafe.Slice(unsafe.StringData(s), len(s))`（string→[]byte 零拷贝读，vfstest.go:541） | 读取用途 → `MemoryMarshal.AsBytes(s.AsSpan())`？**注意**这给出的是 UTF-16 字节！Go 侧拿的是 UTF-8 字节。凡此处涉及**内容哈希/字节输出**（xxh3 入料！主计划 §一.6 钉死哈希 UTF-8 字节）→ 必须 `Encoding.UTF8.GetBytes`/`Utf8.FromUtf16` 真转码，零拷贝优化放弃或改用池化缓冲 |
| 手工 byte buffer 组串（无 unsafe） | 同样优先 `string.Create`；长度不可预知时 `StringBuilder` |

大原则：**UTF-8 世界的零拷贝优化在 UTF-16 移植后逐案消融**——先问"这里的字节是谁的字节"（源文本？输出物？哈希入料？），字节身份由 01-architecture.md 的 JSON/哈希字节保真策略决定，性能其次。

---

## 16. 测试移植约定

框架：xUnit v3（主计划 §三）。断言库 gotest.tools/v3（go.mod:16）→ xUnit 内建 Assert。

### 16.1 结构映射

| Go | C# | 示例 |
|---|---|---|
| `path_test.go` | 镜像目录 `PathTests.cs`，类名 `PathTests` | tests/Ts.Compiler.Tests/TsPath/PathTests.cs |
| `func TestXxx(t *testing.T)` | `[Fact] public void Xxx()` | `TestPathIsRelative` → `[Fact] public void PathIsRelative()`（Test 前缀去掉——类名已含 Tests） |
| `t.Parallel()` | 删（xUnit 集合级并行由 assembly 配置统一管理，08） | tsgo:internal/tspath/path_test.go:659 |
| 表驱动 + `t.Run(name, ...)` | `[Theory]` + `[MemberData]`，name 作首参进 DisplayName | 见 §16.2 |
| `t.Run` 非表驱动子测试（场景分组） | 拆成多个 `[Fact]`，方法名 = 父测试名 + 子名 Pascal 化 | path_test.go:721 `t.Run("empty input", ...)` → `[Fact] public void GetCommonParents_EmptyInput()` |
| `func BenchmarkXxx(b *testing.B)` | BenchmarkDotNet（阶段 10，不设门禁，主计划 §二） | path_test.go:669 |
| `func FuzzXxx` | 不移植（记 06 备注） | parser_test.go:99 |
| `t.Skip(...)` | `Assert.Skip(...)`（xUnit v3 动态跳过） | — |
| `t.Fatalf/Errorf` | `Assert.Fail($"...")`（Fatal/Error 的"继续执行"差异：xUnit 断言即抛，等价 Fatal；Errorf 累积语义丢失，逐案 PORT-NOTE） | — |
| `testing.TB` 参数 | `ITestOutputHelper`（日志）；夹具注入用构造器 | — |
| `t.TempDir()` | 自研 fixture（IDisposable 临时目录），登记 Ts.TestUtil | — |
| `t.Cleanup(f)` | IDisposable / `IAsyncLifetime` | — |
| TestMain / init | 静态构造器或 xUnit `IClassFixture`/AssemblyFixture | path_test.go:649 的 `init()` 变体表加倍 → 数据生成方法内完成 |

### 16.2 表驱动测试范例

```go
// tsgo:internal/tspath/path_test.go:658-667
func TestPathIsRelative(t *testing.T) {
    t.Parallel()
    for _, tt := range pathIsRelativeTests {
        name := shortenName(tt.p)
        t.Run(name, func(t *testing.T) {
            t.Parallel()
            assert.Equal(t, PathIsRelative(tt.p), tt.isRelative)
        })
    }
}
```

```csharp
public class PathTests {
    public static TheoryData<string, bool> PathIsRelativeData() {
        var data = new TheoryData<string, bool>();
        foreach (var (p, isRelative, _) in PathIsRelativeTests) data.Add(p, isRelative);
        return data;
    }

    [Theory]
    [MemberData(nameof(PathIsRelativeData))]
    public void PathIsRelative(string p, bool isRelative)
        => Assert.Equal(isRelative, TsPath.PathIsRelative(p));
}
```

### 16.3 断言映射表（⚠ 参数序相反）

gotest.tools 的约定是 `assert.Equal(t, actual, expected)`（实际值在前）；xUnit 是 `Assert.Equal(expected, actual)`（期望值在前）。**机械替换时必须交换两参**——这是移植测试时最高频的错误源，失败消息里期望/实际颠倒会浪费大量排查时间（见待澄清 #3）。

| gotest.tools | xUnit v3 | 备注 |
|---|---|---|
| `assert.Equal(t, actual, expected)` | `Assert.Equal(expected, actual)` | **交换参数** |
| `assert.NilError(t, err)` | 移除（error→异常后不抛即过）；保留检查时 `ex = Record.Exception(...); Assert.Null(ex)` | tsgo:internal/parser/parser_test.go:74 |
| `assert.Assert(t, cond)` | `Assert.True(cond)` | — |
| `assert.Assert(t, x != nil)` | `Assert.NotNull(x)` | — |
| `assert.DeepEqual(t, actual, expected)`（go-cmp 结构比较） | 首选类型自有相等（record struct ==）；图状结构 `Assert.Equivalent(expected, actual)`；带 cmp.Options 的逐案手写 | 深比较语义差异逐案核对 |
| `assert.ErrorContains(t, err, s)` | `var ex = Assert.Throws<T>(...); Assert.Contains(s, ex.Message)` | — |
| `testutil.AssertPanics(t, fn, expected)`（tsgo:internal/testutil/testutil.go:14-28） | `var ex = Assert.Throws<InvalidOperationException>(fn); Assert.Equal(expected, ex.Message)` | panic 值→异常 Message（§4.3 消息原文保证使其可比） |
| `testutil.RecoverAndFail`（testutil.go:30-35） | 不需要（xUnit 未捕获异常即失败并带栈） | — |

### 16.4 基架与环境

- `race.Enabled`（testutil.go:37-45 决定单/多线程模式）：C# 无 race detector。等价物 = 主计划 §四.3 的确定性双跑（串行 WorkGroup vs 并行）；环境开关定为 `TSCS_SINGLE_THREADED=1`（TSCS_* 前缀族；08-testing-strategy.md §4.2 为权威，本文已同步——裁决 R15。差分基架细节归 08）。
- 基线/黄金文件测试（`baseline.Run` 家族）与 conformance 跑批器：**不属于本节的机械映射**，整体设计见 08-testing-strategy.md。
- Go 测试里的 `//go:embed` 夹具 → `EmbeddedResource` 或拷贝到测试输出目录（逐案，06 登记）。

---

## 17. 注释与标记保留策略

### 17.1 普通注释：逐行随行保留，不翻译不改写

Go 源注释（含大段算法说明，如 checker 的规格引用）**原文（英文）保留**在对应 C# 位置。禁止移植时"顺手改进"注释——注释文本是与上游 diff 对照的一部分。

### 17.2 `// !!!` 标记：原样保留

tsgo 用 `// !!!` 标记"相对 TS 尚未移植/待办"（tsgo:internal/checker/checker.go:10774、14468、23070 等数百处；也出现在签名内嵌注释，如 ast.go:2215 的参数注释）。这些是对照上游 TypeScript 的路标，**逐字保留**（含内嵌于被注释掉的 TS 代码块），使"tsgo 未做的我们也未做"可 grep 验证。**不得**把我们自己的待办也写成 `!!!`——我们的待办用 `// TODO(csport): ...`。

### 17.3 编译器/工具指令注释

| Go 标记 | 处置 |
|---|---|
| `//nolint:xxx`（vfs.go:84、core.go:25/191 等） | **删除**（Go linter 专用）。若同一位置 C# analyzer 报警且该写法是刻意的 → 最窄 `#pragma warning disable XXXX` + 行尾注明理由 |
| `//go:generate`（tsgo:internal/vfs/vfs.go:8） | 删除；等价再生成命令登记 04-codegen.md |
| `//go:embed` | 转 `EmbeddedResource`（07 内嵌资源政策），注释删除 |
| 构建标签 `//go:build darwin` | 转运行时分支/#if（§2.1），标签删除 |
| SAFETY 注释（path.go:666-675） | 随 §15 重写为一行 PORT-NOTE（危险性已被 string.Create 消解） |
| `// TODO(user): ...` | 原样保留（含署名） |

### 17.4 本项目自有标记（新增，全项目统一 grep 面）

| 标记 | 用途 | 规范 |
|---|---|---|
| `// PORT-NOTE: <原因>` | 一切刻意偏离 Go 源的点（§0.5） | 一行说清偏离什么、为何安全；重大者附 06/12 登记 |
| `// map-iter: 序无关` / `// map-iter: 已排序` | §7.3 强制标注 | CI 抽查 |
| `// TODO(csport): ...` | 我方待办 | 与上游 `!!!` 严格区分 |
| 文件头 `// Ported from tsgo internal/<path> (commit <pin>)` | 每个移植文件首行 | 生成文件由生成器自动写入 |

### 17.5 XML doc

不强制。Go 的 `// Foo does X` 导出注释保持 `//` 原文即可；仅 Ts.Compiler 的顶级公共入口（Program、Checker 公共面）可选择性升级为 `/// <summary>`（内容仍为原文）。理由：全量 XML doc 化会把 30 万行的注释 diff 面翻倍，收益为零。

---

## 18. 杂项控制流与语法角落

| Go | C# | 示例/备注 |
|---|---|---|
| 标签循环 `loop: for { ... break loop }` | C# 无标签 break → `goto` 到循环后标签，或提函数。**switch 内 break 出循环**（Go 需标签，C# 的 break 只出 switch）必用 goto | tsgo:internal/scanner/scanner.go:1085-1105（正则扫描 loop 标签）→ `goto loopEnd;` + 循环后 `loopEnd:;`；同型 tsgo:internal/parser/jsdoc.go:222 |
| `continue label` | 循环尾 `goto continueLabel` 或重构 | 罕见，逐案 |
| `goto` | C# `goto` 直译 | tsgo 生产代码几乎不用 |
| `for {}` 无限循环 | `while (true)` | — |
| `for cond {}` | `while (cond)` | — |
| `for i := range n`（int range，Go 1.22） | `for (int i = 0; i < n; i++)` | tsgo:internal/checker/checker.go:19360 `for range len(typeParameters)-1`（弃元形态）→ 同上，弃元则不声明 i |
| `for i, v := range slice` | `for (int i = 0; i < s.Length; i++) { var v = s[i]; ... }`（需索引时）或 `foreach`（只要值） | — |
| 多赋值 `a, b = b, a` | 元组：`(a, b) = (b, a);` | — |
| `iota` 常量组 | enum（连续）或 const 组（有算式则显式展开值） | `Comparison` 组（compare.go:28-32）→ enum 显式 -1/0/1 |
| 包 `init()` | 静态构造器或显式 Initialize（AOT 下警惕静态构造顺序；优先转为 `static readonly` 初始化表达式） | path_test.go:649（测试侧见 §16.1） |
| 闭包捕获循环变量 | Go 1.22+ 每迭代新变量 = C# foreach 语义；C# `for` 循环捕获 i 是**同一变量**——凡 Go `for i := 0; ...` 内起闭包/goroutine，C# 需块内副本 `int i2 = i;` | 审计点 |
| `min/max` 内建 | `Math.Min/Max`（int/long）；泛型处 `T.Min`? → 逐案，多为 int | text.go:64-66 |
| `struct{}` 空结构 | 集合语义 → HashSet（§7.5）；通道元素 → `Channel<bool>` 或信号量（§11） | — |

---

## 待澄清（提交给计划维护者，不改设计）

1. ~~**巨文件拆分表的归属**~~ **已关闭（裁决 R1）**：03-checker-design.md §4.2 的 22 个 partial 文件表为唯一权威，checker 包内 checker.go 之外的文件命名采 03 风格；§2.2 已同步。其余巨文件的逐文件拆分表仍登记 06。
2. **`WorkGroup` 等"接口+封闭实现+工厂"的抽象类化**（§1.3 第三行判据）是本文档给出的翻译判据，主计划仅隐含（行文用无前缀名）。02/06 落型时请逐类型确认（涉及 `vfs.FS` 是否真用 `IFileSystem` 展开名）。
3. **gotest.tools `assert.Equal(t, actual, expected)` 参数序与 xUnit 相反**：若 08 号文档规划任何半自动测试移植脚本，必须内置参数交换；纯手工移植则将此列入 review checklist。归属请 08 确认。
4. **error 文本进基线的完整清单**：§4.2 要求"err.Error() 文本进诊断/基线时逐字复刻 Go 格式"，但哪些 error 文本真正可达基线（tsoptions 解析错误、模块解析 trace、vfs IO 错误）需要在阶段 4/5 逐点核对并登记，建议 12-risks.md 挂总项、06 分包登记。
5. **私有字段 camelCase 无 `_` 前缀**（§1.1）与 .NET 默认 IDE1006/常见 StyleCop 规则冲突：需要阶段 0 的 `.editorconfig`/analyzer 配置明确豁免（归 10-phase-0-scaffolding.md 的 WBS 一项）。主计划样例代码支持本裁定但未明文。
6. **`strings.EqualFold`/`unicode.ToLower` 与 BCL 的 Unicode 表版本差异**（§8.4）：本文要求逐行移植手写比较器，但 `Rune.ToLowerInvariant` 底层 Unicode 数据版本与 Go unicode 包可能不同步。是否需要像标识符表那样固化一份 Go 侧 casing 表（04 生成族候选），请 12-risks.md 评估后定。
7. **`Node.Text()` 判为方法**（§1.2）：02 的生成器访问器清单若已将其列为属性，以 02 为准并回改本文；本文判据（多路 switch + panic 分支）倾向方法。
8. **稳定排序审计**（§6.4）：Go `slices.SortStableFunc` 与 `sort.SliceStable` 的使用点清单及其是否输出承重，需 06 逐包登记；`Array.Sort` 不稳定这一差异容易漏。**已登记**：12-risks.md 已增列 R22 挂此审计总项（06 逐包清单义务不变）。

---

*本文档为活文档：移植中每遇到一个本文未覆盖的惯用法，解决后必须回填对应章节（PR 同时改代码与本文）。*
