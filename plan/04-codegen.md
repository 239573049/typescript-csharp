# 04 — 代码生成管线（Codegen Pipeline）

> 版本 v1.0 · 日期 2026-07-11 · 状态 Draft
>
> 本文是主计划 §一.4「代码生成清单」表的全文展开。读者是将要动手实现 `tools/generator/` 与移植各生成族的工程师。所有对 Go 源码的论断均附 `文件:行号`（相对 `/Users/token/Desktop/code/typescript-go-main/`，下称 *tsgo 仓*）。交叉引用：架构决策见 01-architecture.md，AST 设计全文见 02-ast-design.md，Go→C# 惯用法见 05-conventions.md，AOT 约束见 07-aot-constraints.md，测试与新鲜度门禁归属见 08-testing-strategy.md，模块归属见 06-module-inventory.md。

---

## 目录

- §0 使命与总原则
- §1 生成器工作区 `tools/generator/`（布局、package.json、npm scripts 总表）
- §2 AST 生成族 — `generate-csharp-ast.ts`（与 generate-go-ast.ts 的差异清单、7 个输出文件划分、ast.json/schema.ts 复用与所有权）
- §3 诊断生成族 — `generate-diagnostics.ts`（convertPropertyName 逐条复刻、2,154 计数校验、extraDiagnosticMessages.json、13 语言 .lcl 管线）
- §4 libs 生成族 — `generate-libs.ts`（108 个 lib.d.ts → EmbeddedResource）
- §5 LSP 元模型族 — `fetch-lsp-model.ts` + `generate-lsp.ts`（OR 类型 JsonConverter 策略、STJ 源生成上下文组织）
- §6 Unicode 表族 — `transcribe-unicode.ts`（一次性转写：决定与理由）
- §7 枚举 stringer 族 — `generate-stringers.ts`（承重判据 + 初始清单）
- §8 API 二进制编码器族 — `generate-csharp-encoder.ts`（阶段 9）
- §9 生成物通用规范（文件头标记、格式化政策、AOT、CI 新鲜度检查）
- §10 上游同步 SOP（submodule/tsgo pin 升级时的统一流程）
- §11 待澄清

---

## §0 使命与总原则

tsgo 中约 37,000 行 Go 生产代码是机器生成的（`ast_generated.go` 10,047 行、`kind_generated.go` 463 行、`diagnostics_generated.go` 8,626 行、`loc_generated.go` 151 行、`lsp_generated.go` 17,262 行、`encoder_generated.go` 707 行、unicode 表 4,887 行、各 stringer 若干）。这些代码**不逐行手工移植**——我们移植/新写它们的**生成器**，让 C# 侧产物与 Go 侧产物由同一份数据源（ast.json、diagnosticMessages.json、libs.json、metaModel.json、@unicode 表）推导，从根上消灭手抄漂移。

七大铁律（对全部生成族生效）：

1. **离线脚本 + 产物入库**。与 tsgo 的 `go generate` + 提交产物的哲学一致（tsgo 仓 `Herebyfile.mjs:299-305` 的 `generate` task 即 `go generate -v ./...`）。**不使用 Roslyn 源生成器**承担这些工作，理由见主计划 §一.1：diff 可审、可断点调试、万行级内存生成物拖垮 IDE。唯一的例外是 System.Text.Json 的 `JsonSerializerContext`（这是 STJ AOT 的强制形态，见 §5.5 与 07-aot-constraints.md）。
2. **生成器用 TypeScript 写，Node 运行**，与上游脚本同技术栈（tsgo 全部生成脚本都是 `node --experimental-strip-types` 可跑的 .ts/.mts），使上游脚本更新时 diff 移植成本最低。
3. **上游数据资产要么走 submodule 现场读取，要么拷贝入仓并声明所有权**（每族一节写明是哪种），禁止"半同步"状态。
4. **每个生成族有唯一 npm script**，可单独重跑；`gen:all` 聚合日常族（排除一次性族与需网络的 fetch）。
5. **产物可再生等幂**：同一输入两次运行字节相同（无时间戳、无 map 迭代序依赖、gzip 参数固定）。这是 CI 新鲜度检查（§9.4）的前提。
6. **生成物遵守 AOT 铁律**（07-aot-constraints.md）：CI 对 `*.g.cs` 同样跑 AOT/Trim analyzer（主计划阶段 0 门禁：`Ast/*.g.cs` 过 AOT+Trim analyzer）。
7. **对照优先于惯例**：凡 Go 产物中的标识符会被人拿来 grep 对照（诊断成员名、AST 节点名、union 成员名），C# 产物保持**同名**，即使违反 C# 命名惯例——此类例外逐条记入 05-conventions.md。

生成族总表（主计划 §一.4 的落地版）：

| # | 族 | 输入 | 脚本 | 输出 | 阶段 | npm script |
|---|---|---|---|---|---|---|
| 1 | AST（192 节点/35 base/351 kind） | `tools/generator/ast.json`（入仓副本） | `generate-csharp-ast.ts` | `src/Ts.Compiler/Ast/*.g.cs` ×7（含 `SyntaxKind.g.cs`、`GlobalUsings.g.cs`，§2.4） | 0 | `gen:ast` |
| 2 | 诊断 2,154 条 + 13 语言 | 子模块 `diagnosticMessages.json` + `extraDiagnosticMessages.json`（入仓副本）+ 子模块 `.lcl` | `generate-diagnostics.ts` | `Diagnostics/Messages.g.cs`、`Diagnostics/Loc.g.cs`、`Diagnostics/loc/*.json.gz` ×13 | 0 | `gen:diagnostics` |
| 3 | lib.*.d.ts ×108 | 子模块 `src/lib/` | `generate-libs.ts` | `Bundled/libs/*.d.ts` ×108 + `Bundled/LibNames.g.cs` | 0 | `gen:libs` |
| 4 | LSP 协议 | `tools/generator/metaModel.json`（入仓副本，见 §5.1） | `generate-lsp.ts` | `src/Ts.LanguageService/Lsp/Protocol.g.cs` | 8 | `gen:lsp` |
| 5 | Unicode 表 | tsgo 生成表（一次性） | `transcribe-unicode.ts` | `StringUtil/JsCase.g.cs`、`StringUtil/IdentifierParts.g.cs` | 1 | `gen:unicode`（不在 gen:all） |
| 6 | 枚举 stringer | `tools/generator/stringers.json` + ast.json | `generate-stringers.ts` | 各处 `*Stringers.g.cs` | 随宿主包 | `gen:stringers` |
| 7 | API 二进制编码器 | `tools/generator/ast.json`（同 #1 副本） | `generate-csharp-encoder.ts` | `Api/Encoder.g.cs`、`Api/Decoder.g.cs` | 9 | `gen:encoder` |

不移植的上游生成器（记录以免误纳）：`_scripts/generate-ts-ast.ts`（1,705 行，为 `_packages/native-preview` npm 包生成 TS 侧 AST API）与 `Herebyfile.mjs` 的 `generate:enums`（`Herebyfile.mjs:339-556`，为 npm 包生成 TS 枚举）——两者服务 TS 客户端包，不在三个 C# 源项目范围内；`internal/testutil/projecttestutil` 与 `internal/vfs` 的 moq mock 生成（`projecttestutil.go:26-30`、`vfs.go:8-9`）——C# 测试用手写 fake 或 NSubstitute 等按 08-testing-strategy.md 处理，不进本管线。

---

## §1 生成器工作区 `tools/generator/`

> 本节是 `tools/generator/` 工作区（布局、package.json、npm scripts、依赖 pin）的**唯一权威**（评审裁决 R13）；10-phase-0-scaffolding.md §1.11 已收敛至此。

### 1.1 目录布局

```
tools/generator/
  package.json               # 见 §1.2
  package-lock.json          # 锁定，入仓
  tsconfig.json              # 移植 tsgo _scripts/tsconfig.json（nodenext/esnext/noEmit/
                             #   verbatimModuleSyntax/erasableSyntaxOnly/allowImportingTsExtensions）
  README.md                  # 指向本文档
  # ── 上游资产入仓副本（所有权见各节）─────────────────────
  ast.json                   # ← tsgo _scripts/ast.json 逐字节副本
  ast.schema.json            # ← tsgo _scripts/ast.schema.json 逐字节副本
  schema.ts                  # ← tsgo _scripts/schema.ts 逐字节副本（1,267 行，只读复用，见 §2.2）
  extraDiagnosticMessages.json  # ← tsgo internal/diagnostics/extraDiagnosticMessages.json 副本（33 条）
  metaModel.json             # ← 由 fetch-lsp-model.ts 拉取后入仓（与上游 gitignore 政策相反，理由见 §5.1）
  metaModelSchema.ts         # ← tsgo internal/lsp/lsproto/_generate/metaModelSchema.mts 副本
  lsp-model-version.json     # { "vscode-languageclient": "10.0.1" } 显式 pin（见 §5.1）
  stringers.json             # stringer 族声明清单（见 §7.3）
  expected-counts.json       # { "diagnostics": 2154, "libs": 108, "locales": 13, "astNodes": 192, "astBases": 35 }
  # ── 生成脚本 ─────────────────────────────────────────
  csharp-emit.ts             # C# 类型格式化/标识符转义/CodeWriter 公共层（见 §1.3）
  generate-csharp-ast.ts
  generate-diagnostics.ts
  generate-libs.ts
  fetch-lsp-model.ts
  generate-lsp.ts
  transcribe-unicode.ts
  generate-stringers.ts
  generate-csharp-encoder.ts
```

### 1.2 package.json（npm scripts 设计）

```jsonc
{
  "private": true,
  "name": "typescript-csharp-generator",
  "type": "module",
  "engines": { "node": ">=22.11" },
  "devDependencies": {
    "@types/node": "latest",
    "typescript": "^5.8.0",
    "fast-xml-parser": "5.0.9"        // 仅 .lcl 解析用；精确 pin（无 ^），见 §3.6
  },
  "scripts": {
    "gen:ast":         "node --experimental-strip-types generate-csharp-ast.ts",
    "gen:diagnostics": "node --experimental-strip-types generate-diagnostics.ts",
    "gen:libs":        "node --experimental-strip-types generate-libs.ts",
    "gen:lsp":         "node --experimental-strip-types generate-lsp.ts",
    "gen:lsp:fetch":   "node --experimental-strip-types fetch-lsp-model.ts",
    "gen:stringers":   "node --experimental-strip-types generate-stringers.ts",
    "gen:encoder":     "node --experimental-strip-types generate-csharp-encoder.ts",
    "gen:unicode":     "node --experimental-strip-types transcribe-unicode.ts",
    "gen:all":         "npm run gen:ast && npm run gen:diagnostics && npm run gen:libs && npm run gen:lsp && npm run gen:stringers && npm run gen:encoder",
    "check":           "tsc -p tsconfig.json"
  }
}
```

设计要点：

- `gen:all` **不含** `gen:unicode`（一次性族，输入是 tsgo 生成表而非本仓资产，见 §6）与 `gen:lsp:fetch`（需网络，仅升级 LSP 模型 pin 时手动跑）。
- 各脚本内部以 `import.meta.dirname` 定位仓库根（模式同 `_scripts/generate-go-ast.ts:35` 的 `ROOT`），**绝不依赖 cwd**。
- 阶段 0 只需 `gen:ast`、`gen:diagnostics`、`gen:libs` 可用（主计划阶段 0 门禁"生成器自举（ast+diagnostics+libs）"）；`gen:lsp` 在阶段 8 前、`gen:encoder` 在阶段 9 前允许是空壳脚本（直接 `process.exit(0)` 并打印 "not yet implemented"），以便 `gen:all` 与 CI 新鲜度检查从第 0 天起就能全量运行。
- 所有脚本运行完打印每个产物路径 + 行数 + 关键计数（供门禁日志引用）。

### 1.3 公共层 `csharp-emit.ts`

上游每个脚本各自内嵌一个 `CodeWriter`（`generate-go-ast.ts:62-89`、`generate-encoder.ts:30-54`）。我们抽出唯一公共层，职责：

- `CodeWriter`：行缓冲 + 4 空格缩进（Go 侧用 tab，C# 侧遵循 .editorconfig 的 4 空格）；`{`/`}` 块辅助（`block(header, fn)`）。
- `csIdentifier(name)`：C# 关键字转义（`params`→`@params`、`operator`→`@operator` 等；对应 Go 侧 `goParamName` 的关键字避让，`schema.ts:718-720`）。**JSDoc 专名原样保留**：凡含 `JSDoc` 的标识符与文件名（`JSDocTagBase`、`AsJSDocText`、`IsJSDocKind` 等）逐字保留，不适用任何缩写/重拼规则（评审裁决 R6：grep 对照性 + Go 实名一致；登记于 05-conventions.md 的专名例外清单）。
- `csStringLiteral(s)`：产出 C# 字符串字面量。规则：默认普通字面量 + `\uXXXX` 转义所有非 ASCII 与控制字符——保证 `.g.cs` 文件本身是纯 ASCII，规避编辑器/工具链编码歧义（诊断文本、locale 文本含大量非 ASCII）。
- `autoGeneratedHeader(toolName, inputs[])`：统一文件头（见 §9.1）。
- `writeIfChanged(path, content)`：内容不变时不触碰文件（保 mtime，避免无谓重编译）。

各生成脚本**不得**内嵌自己的 writer 变体。

---

## §2 AST 生成族 — `generate-csharp-ast.ts`

### 2.1 输入与所有权

| 资产 | 来源 | 所有权规则 |
|---|---|---|
| `tools/generator/ast.json` | tsgo `_scripts/ast.json`（35 base、192 节点、72 node alias、351 kind 元素——已程序化核验） | **逐字节副本**。禁止本地编辑；唯一修改途径是 §10 的上游同步流程。副本 ≠ fork：任何"想给 C# 加点元数据"的冲动一律改为在 `generate-csharp-ast.ts` 内写映射表 |
| `tools/generator/ast.schema.json` | tsgo `_scripts/ast.schema.json` | 同上（仅供编辑器校验 ast.json） |
| `tools/generator/schema.ts` | tsgo `_scripts/schema.ts`（1,267 行） | **逐字节副本、只读复用**。它是 ast.json 的解析/查询层，与目标语言基本无关；其中 Go 特定的格式化方法（`formatGoReference`、`formatGoConstant`、`goParamName`）**不调用也不删除**——保持逐字节一致让上游同步是纯拷贝。C# 侧的类型格式化全部写在 `csharp-emit.ts` |

`generate-csharp-ast.ts` 通过 `schema.ts` 的公开 API 读取模型（与 `generate-go-ast.ts` 相同的调用面）：`api.nodes()`（schema.ts:873）、`api.bases()`（:864）、`api.nodeAliases()`（:890）、`api.listAliases()`（:900）、`api.kindElements()`（:904）、`api.kindMarkers()`（:913）、`api.kindAliases()`（:925）、`api.kindGuards()`（:956）、`api.expandKindAliasMembers()`（:970）、`api.uncapitalize()`（:859，注意 JSDoc 前缀特判）。

### 2.2 与 `generate-go-ast.ts`（1,028 行）的差异清单

下表逐生成面对照。"保留"= 逻辑 1:1 直译仅换发射语法；"改写"= 输出形态因 C#/继承而不同；"删除"= 该生成面在 C# 中无对应物。

| # | Go 生成面（generate-go-ast.ts 位置） | 处置 | C# 形态 |
|---|---|---|---|
| 1 | 文件头 `generateHeader`（:122-138） | 改写 | §9.1 统一头 + `#nullable enable`；无 import 防未用技巧（`var _ = core.Same[string]`，:135-136 —— C# 不需要） |
| 2 | `generateNodeFactoryStruct`（:91-120）：hooks + **~50 个 arena 字段** + nodeCount/textCount | 改写 | **全部 arena 字段删除**（主计划 §一.1：GC 取代 arena）。`NodeFactory` 保留 `hooks`、`nodeCount`、`textCount` 三个成员。`node.arena` 标志在 C# 生成器中读到也**忽略** |
| 3 | `generateBaseStructDefs`（:224-259）：所有 base 平铺为 struct + 嵌入 | **核心改写** | 按三分类发射（见 §2.3）：Node 系 base → 抽象类单继承链；数据 mixin → 同名接口 + 字段扁平；brand base → 空标记接口。`HAND_WRITTEN_BASES = {NodeBase}`（:222）对应 C# 手写 `Ast/Node.cs`（主计划 §一.1 的 Node 定义），生成器跳过 |
| 4 | 节点 struct `generateStructDef`（:142-171）：嵌入列表 + 字段 | 改写 | `public sealed partial class Xxx : <最派生Node系base>, <mixin接口…>`；仅发射本节点新增字段（去除 base 链与 mixin 扁平化已覆盖者）；`noGo` 成员跳过、`goOnly` 成员用 `rawType` 的 C# 映射（映射表在 csharp-emit.ts，遇到未知 rawType 直接抛错拒绝生成）；`m.optional` → 字段类型可空 + `// Optional` 注释（:173-178 对应） |
| 5 | 类型别名 ×3 组（:781-843）：`FooNode = Node`、instantiation alias（`EndOfFile = Node`）、`StatementList = NodeList`、union alias（`Expression = Node`） | 改写 | 全部 → `global using` 到 **`Ast/GlobalUsings.g.cs`**：`global using Expression = Ts.Ast.Node;`、`global using StatementList = Ts.Ast.NodeList;` 等，并保留 Go 侧的注释（union 成员列表）。跨项目共享：Ts.LanguageService/测试项目通过 `<Compile Include="…/Ast/GlobalUsings.g.cs" Link="…"/>` 引入（写入各 csproj，阶段 0 脚手架落实） |
| 6 | `generateAsCast`（:263-271）：`n.data.(*X)` | 改写 | `public X AsX() => (X)this;` ×192 于 Node 的 partial（失败抛 InvalidCastException ≙ Go panic，主计划 §一.1）。Token 等多 Kind 共享类照旧只有一个 AsToken |
| 7 | `generateNewFactory`/`emitNewFactory`（:280-346）：`data := f.xxxArena.New()` 或 `&X{}`，字段赋值、bitmask 掩码、textCount++、NodeFlags 成员回填 node.Flags、kindAliases 生成多个 NewXxx | 保留 | `var data = new X();` 一条路（arena 分支消失）；`data.Y = y & mask`、`node.Flags |= flags & mask`、`f.NewNode(kind, data)` 逐行对应；`hasTextContent`（:53-56）触发 `textCount++` 照搬 |
| 8 | `generateUpdateFactory`（:350-421）：slice 用 `!core.Same(...)` 比较，其余 `!=`；kindAliases 走 Kind switch + default panic（:406） | 保留 | slice 身份比较 → `!ReferenceEquals(a, node.Y)`（Go slice 身份 ≙ C# 数组引用，05-conventions.md）；`panic(...)` → `throw new InvalidOperationException($"unexpected kind in UpdateX: {node.Kind.ToDisplayString()}")`（注意用 §7 的 ToDisplayString 保持消息文本与 Go 一致） |
| 9 | `generateForEachChild`（:425-479）+ **`generateForEachChildDispatch`（:506-527）** | 改写/**删除** | 每节点生成 `public override bool ForEachChild(Visitor v)`（visit/visitNodes/visitNodeList/visitModifiers 链照搬；`handWrittenVisitor` 节点仍委托手写 `ForEachChild_X`，:439-445 对应）。**Kind-switch 总调度整个删除**：其存在理由是 Go 接口调用对逃逸分析不透明导致访问者闭包上堆（:495-505 的注释）；C# 虚方法调用无此问题，直接虚分派即是等价且更简的形态。无孩子节点不 override，继承 Node 上返回 false 的默认实现 |
| 10 | `generateVisitEachChild`（:531-594） | 保留 | `public override Node VisitEachChild(NodeVisitor v)` override；`core.SameMap` → `Ts.Core.CoreUtil.SameMap`（阶段 1 移植）；`m.visit` 定制访问方法名、visitToken/visitModifiers/visitNodes 分支照搬 |
| 11 | `generateClone`（:598-647） | 保留 | override + kindAliases switch + panic 直译同 #8；`node.Modifiers()` 特判（:613-615）照搬 |
| 12 | `generateSubtreeFacts`（:191-217） | 保留 | `internal override SubtreeFacts ComputeSubtreeFacts()`；三种传播项（:180-189）照搬 |
| 13 | `generateNameAccessor`（:747-765）：私有 name 字段的 Name() | 改写 | private 字段 + `public DeclarationName? Name => name;`（getter→属性规则，05-conventions.md） |
| 14 | `generateIsFunction`（:651-696）+ `generateKindAliasGuards`（:706-743，含 skip `IsJSDocKind` :709） | 保留 | 包级函数 → 静态类静态方法，宿主静态类定稿为 **`Ts.Ast.NodeTests`**（评审裁决 R7：对应原版 TypeScript 的 factory/nodeTests.ts，域忠实；原 `Ts.Ast.AstFacts` 占位废弃）；multi-kind、typeParameter kind、kindAliases、range/enumerated guard 四种形态逐一对应；`IsJSDocKind` skip 照搬（与手写 utilities 冲突的语义在 C# 同样存在） |
| 15 | `generateKind`（:904-1006）：`type Kind int16` + iota + KindCount + markers + kind union aliases | 改写 | `public enum SyntaxKind : short { Unknown, EndOfFile, …, Count, FirstAssignment = EqualsToken, … }`（C# 枚举天然允许重复值成员 ≙ marker；落独立 `Ast/SyntaxKind.g.cs`，见 §2.4）；注释元素（:951-953）转 `// …`；kind union alias（:979-1002）→ `global using`（进 GlobalUsings.g.cs），保留展开成员注释；marker/union 引用校验逻辑（:923-945）**原样保留**（生成期断言） |
| 16 | 落盘 `writeAndFormat`：dprint fmt（:1008-1012） | 删除 | 生成器直接发射最终格式（§9.2），无后置格式化器 |

补充规则（不在上表的横切项）：

- **可空性**：字段/参数的 C# 可空标注遵循 02-ast-design.md 的规则（默认镜像 Go 指针 → 可空引用类型；`m.optional` 只影响注释与文档，不改变可空标注）。生成器不做"聪明"的非空推断。
- **partial 划分**：每个节点类声明 `sealed partial`，成员按 §2.4 落入不同文件；同一类跨文件的 partial 声明基类/接口列表只在 Nodes.g.cs 写一次。
- **handWritten 节点**（如 SourceFile，`generate-go-ast.ts:853-867`）：类与工厂手写在 `Ast/Ast.cs`（对应 Go ast.go），生成器只为其发射 Is* 守卫与 As* 转换，并发射注释 `// Struct and factory methods hand-written in Ast/Ast.cs`。

### 2.3 基类选取三规则（生成器必须实现，主计划 §一.1 已对全模型核验）

生成器启动时先对 35 个 base 做一次**血缘分类**（结果缓存）：

1. **Node 系判定**：base 的 extends 传递闭包含 `NodeBase` ⇒ Node 系（18 个）→ 抽象类；否则若自身与闭包均无字段 ⇒ brand（4 个：TypeSyntaxBase/TypeElementBase/ClassElementBase/ObjectLiteralElementBase）→ 空标记接口；否则 ⇒ 数据 mixin（13 个）→ 接口 + 字段扁平。
2. **基类选取**：节点 extends 列表中的 Node 系 base 取**最派生者**（= 其闭包包含列表中所有其他 Node 系 base 者）。已知 4 个节点冗余重列祖先：UnionTypeNode/IntersectionTypeNode（丢弃冗余 TypeNodeBase，取 UnionOrIntersectionTypeNodeBase）、FunctionTypeNode/ConstructorTypeNode（取 FunctionOrConstructorTypeNodeBase）；这两个中间 base 携带字段/mixin，必须发射为链上抽象类而非接口。**若发现无法总序化的 extends 列表（互不包含），生成器抛错**——这是上游模型变化的信号，人工升级处理。
3. **mixin 扁平化按字段名去重**：对节点的 mixin 传递闭包做 BFS，字段按名字去重取**最浅深度**的一份（复刻 Go 嵌入提升规则；DeclarationBase 经两条路径到达的 13 处菱形由此归一）；再对照已选定基类链已有字段二次去重（基类链里已有的不再发射）。节点的接口实现列表 = 闭包中全部数据 mixin + brand 接口（含传递引入者，显式全列——让 `node is DeclarationBase` 型判定与 Go 的接口断言一致可 grep）。

mixin 的三件套发射（以 DeclarationBase 为例）：

```csharp
// Nodes.g.cs — 接口本体（不加 I 前缀：主计划确认的命名例外，登记于 05-conventions.md）
public interface DeclarationBase { Symbol? Symbol { get; set; } }

// Nodes.g.cs — 携带者：字段扁平为自动属性
public sealed partial class ClassDeclaration : ClassLikeDeclarationBase, DeclarationBase, … {
    public Symbol? Symbol { get; set; }          // ← DeclarationBase 扁平
    …
}

// Accessors.g.cs — Node 上的虚访问器 + 携带者 override
public abstract partial class Node {
    public virtual DeclarationBase? DeclarationData() => null;
}
public sealed partial class ClassDeclaration {
    public override DeclarationBase DeclarationData() => this;
}
```

调用点由此逐字保真：Go `node.DeclarationData().Symbol = sym` → C# 同文。虚访问器一览（Node 上各一，已按 Go `ast.go:1181-1202` 的 nodeData 接口实名逐一核对，与 02-ast-design.md §4.2/§8.2 核验表同文）：**9 个 `*Data()`**——FlowNodeData/DeclarationData/ExportableData/LocalsContainerData/FunctionLikeData/ClassLikeData/BodyData/LiteralLikeData/TemplateLiteralLikeData；另加 Modifiers()/SetModifiers() 与 Name()。

### 2.4 7 个输出文件的内容划分（评审裁决 R2 定稿，与 02-ast-design.md §16 同文）

| 文件 | 内容 | 量级估计 |
|---|---|---|
| `Ast/Nodes.g.cs` | 4 brand 接口；13 mixin 接口；18 Node 系抽象 base 类（字段+mixin 扁平）；192 节点类主声明（继承/接口列表、字段、mixin 自动属性、私有 name 的 Name 属性） | ~5.5k 行 |
| `Ast/NodeCasts.g.cs` | Node partial：As* ×192；静态守卫类：Is* 函数（单 kind/multi-kind/kindAliases）+ kind alias guards（IsTokenKind 等 range/switch） | ~2.5k 行 |
| `Ast/NodeFactory.g.cs` | NodeFactory 类（hooks/nodeCount/textCount）+ New\*（193 个，含 kindAliases 变体）+ Update\* | ~4k 行 |
| `Ast/Visitors.g.cs` | 各节点 partial：ForEachChild/VisitEachChild/Clone/ComputeSubtreeFacts overrides | ~4k 行 |
| `Ast/Accessors.g.cs` | Node partial：9 个 `*Data()` 虚访问器（+ Modifiers()/SetModifiers()/Name()，§2.3）+ 携带者 override；多态访问器 switch（Go 手写在 ast.go 的 Expression()/Type()/Name() 等中由 schema 驱动的部分——以 generate-go-ast.ts 实际生成面为界：**Go 侧未生成的多态访问器在 C# 侧同样手写**，不扩大生成面） | ~1.5k 行 |
| `Ast/SyntaxKind.g.cs` | `SyntaxKind` 枚举（含 Count 与 marker 成员，§2.2 #15） | ~0.5k 行 |
| `Ast/GlobalUsings.g.cs` | 三组别名 global usings（node alias/instantiation alias/list alias/union alias/kind union alias） | ~0.4k 行 |

> 注：评审裁决 R2 定稿为 **7 个物理文件**（`SyntaxKind.g.cs` 独立 + `GlobalUsings.g.cs`），与 02-ast-design.md §16 同文；主计划"~5 个"是约数，以 7 为准。§11-1 据此关闭。

### 2.5 再生成命令与上游同步

- 再生成：`npm run gen:ast`（tools/generator 下）。生成末尾断言：节点数 == expected-counts.json 的 astNodes（192）、base 数 == 35、kind 元素数 == 351，任一不符即失败——升级上游时先改 expected-counts.json（diff 审查的显式化）。
- 上游同步（并入 §10 总 SOP）：tsgo pin 前进后 `diff` 上游 `_scripts/{ast.json, ast.schema.json, schema.ts}` 与本仓副本 → 逐字节拷贝 → 若上游 `generate-go-ast.ts` 也变了，人工审阅其 diff 并同步移植到 `generate-csharp-ast.ts` → `npm run gen:ast && npm run gen:encoder && npm run gen:stringers`（三族共享 ast.json）→ 审查 `git diff -- 'src/Ts.Compiler/Ast/*.g.cs'` → 跑阶段 3 的 AST 转储差分测试。

---

## §3 诊断生成族 — `generate-diagnostics.ts`

对应 Go 生成器 `internal/diagnostics/generate.go`（461 行，`//go:generate go run generate.go -diagnostics ./diagnostics_generated.go -loc ./loc_generated.go -locdir ./loc`，diagnostics.go:16）。

### 3.1 输入

1. `_submodules/TypeScript/src/compiler/diagnosticMessages.json`——**submodule 现场读取**（generate.go:84 同款路径）。
2. `tools/generator/extraDiagnosticMessages.json`——tsgo `internal/diagnostics/extraDiagnosticMessages.json` 的**入仓副本**（33 条：tsgo 自有 CLI/LS 消息 code 100000-100019 段 + 对 Strada 消息的覆盖/增补如 5090/8030/5074 等）。所有权：副本，同步走 §10。
3. `_submodules/TypeScript/src/loc/lcl/*/diagnosticMessages/diagnosticMessages.generated.json.lcl`——13 个语言的 LocStudio XML（generate.go:201 同款 glob；当前 13 语言：cs-CZ、de-DE、es-ES、fr-FR、it-IT、ja-JP、ko-KR、pl-PL、pt-BR、ru-RU、tr-TR、zh-CN、zh-TW）。

### 3.2 合并与排序语义（必须逐字复刻）

- 两个 JSON 均解析为 `key 文本 → {category, code, …}`；随后**按 code 建索引**（generate.go:354-357 的 `codeToMessage[m.Code] = m`——同文件内 code 冲突时后读的键悄悄胜出，保持此语义）；`maps.Copy(main, extra)`（:94）⇒ **extra 按 code 覆盖 main**（extra 中的 5090、8030、6353、6401、6420、6423、6424、6425、6048、1549、5112、5074、5002 正是靠这一覆盖生效）。
- 合并后按 code 升序排序（:97-99）→ 决定产物中成员的声明顺序。
- **计数校验**：排序后断言总数 == expected-counts.json 的 `diagnostics`（当前 **2,154**，与 tsgo `diagnostics_generated.go` 中 `grep -c '^var '` == 2154 一致）。数值不符 → 生成失败，升级上游时显式更新。

### 3.3 convertPropertyName 逐条复刻（generate.go:408-460）

这是**成员名与 key 的唯一权威算法**，C# 侧必须产出与 Go 完全相同的成员名（对照/grep 保真，主计划要求）。按序执行：

| 步 | 规则 | Go 依据 | 示例 |
|---|---|---|---|
| 1 | 逐**码点**（Go `for range` 按 rune）扫描原文：`*`→`_Asterisk`；`/`→`_Slash`；`:`→`_Colon`；其余非字母且非数字（`unicode.IsLetter/IsDigit`，Unicode 类别语义，不是 ASCII）→ `_`；字母数字原样 | :412-427 | `'*/' expected.` → `__Asterisk_Slash__expected_` |
| 2 | 多下划线折叠：`/_+/g` → `_` | :403,:431 | → `_Asterisk_Slash_expected_` |
| 3 | 去前导下划线，**除非其后是数字**：`/^_+(\D)/` → `$1`（`'{0}' expected.` 的 `_0_expected` 保留前导 `_`） | :404,:433 | → `Asterisk_Slash_expected_` |
| 4 | 去尾随下划线：`/_$/` → `` | :405,:435 | → `Asterisk_Slash_expected` |
| 5 | **key**（本地化查找键）= 此时的 varName 截断到 ≤100 字符，再拼 `"_" + code` | :437-441 | `Asterisk_Slash_expected_1010`；超长例：code 1202 的 key 在 100 字符处截断（tsgo diagnostics_generated.go:319） |
| 6 | 若 varName 非 Go 导出形（`token.IsExported`：首**码点**不是 Unicode 大写字母）：以 `_` 开头则前缀 `X`，否则前缀 `X_` | :443-453 | `_0_expected` → `X_0_expected`；`export_Asterisk_does_not_re_export_a_default` → `X_export_Asterisk_does_not_re_export_a_default`（tsgo 产物 :307） |
| 7 | 终校验：必须是合法导出标识符，否则 fatal | :455-457 | C# 侧同样断言（C# 标识符集是超集，X_ 前缀后必然合法，断言用于防未来上游输入畸变） |

实现注意：

- 第 6 步的 `IsExported` 判定在 C# 生成器里**按 Go 语义实现**（首码点 `Lu` 类别判定），不得用"C# 是否需要转义"替代——否则成员名会与 Go 漂移。
- 第 1 步的字母/数字判定必须用 Unicode 类别（JS `/\p{L}|\p{N}/u`），与 Go `unicode.IsLetter/IsDigit` 对齐。
- key 的 100 字符截断按**字符（码点？字节？）**——Go `key[:100]` 是**字节**切片；诊断键当前全 ASCII 故无歧义，但生成器按**UTF-8 字节**截断实现并加断言"截断点不得落在多字节序列中间"，与 Go 完全一致。

### 3.4 产物 `Diagnostics/Messages.g.cs`

```csharp
public static class Diagnostics
{
    public static readonly Message Unterminated_string_literal = new(code: 1002, category: Category.Error, key: "Unterminated_string_literal_1002", text: "Unterminated string literal.");
    public static readonly Message X_0_expected = new(code: 1005, category: Category.Error, key: "_0_expected_1005", text: "'{0}' expected.");
    // … ×2,154，按 code 升序；reportsUnnecessary/elidedInCompatibilityPyramid/reportsDeprecated
    //   仅在为 true 时以命名实参附加（对应 generate.go:149-157 的条件发射）
}
```

- 成员名 = §3.3 的 varName **原样**（`X_0_expected`、`Asterisk_Slash_expected`——违反 C# 命名惯例是**有意的**，登记 05-conventions.md 例外表；`[sic]` 同 Go：上游 JSON 里 `elidedInCompatabilityPyramid` 的拼写错误在反序列化层容忍，成员名用正确拼写，generate.go:36-37 同款处理）。
- `Message` 类型手写于 `Diagnostics/Diagnostics.cs`（移植 diagnostics.go 的 Message struct 与 getter，:44-60），生成文件只含实例与查找函数。
- `KeyToMessage`：直译 generate.go:162-171 的 switch → C# `public static Message? KeyToMessage(string key) => key switch { "Unterminated_string_literal_1002" => Unterminated_string_literal, …, _ => null };`（string switch 编译为哈希跳转，AOT 安全）。
- `Category` 枚举 + 手写 `Name()`（"warning"/"error"/"suggestion"/"message"，diagnostics.go:29-41）**手工移植**，不属本生成族（见 §7 判据表——它是 errors.txt 承重，但源头是手写函数而非 stringer）。

### 3.5 13 语言 `.lcl → json.gz → EmbeddedResource` 管线

复刻 generate.go:176-338 + :363-400：

1. glob §3.1-3 的 .lcl 文件并**排序**（:208，路径序 = 输出确定性）。
2. 解析 XML：三层 Item 嵌套；`ItemId` 形如 `";key_code"`，去前导分号（:384）；取 `Str/Tgt/Val`（已翻译）否则回退 `Str/Val`（:388-392）。解析器用 `fast-xml-parser`（精确 pin；tsgo 用 encoding/xml——我们只需属性+文本提取，任何合规 XML 解析器等价）。
3. 过滤：仅保留 key ∈ knownKeys（= §3.3 对全部 2,154 条算出的 key 集，:102-106、:227-232）。
4. 序列化：key **排序**后（:259）写 JSON（紧凑、UTF-8；键值对顺序确定 ⇒ 产物字节确定。注意：与 Go jsonv2 产物字节允许不同——这些 .gz 是**我方自产自销**的资源，运行时只消费解析结果，非对照承重面；但**同输入自身必须字节稳定**以满足新鲜度检查）。
5. gzip：Node `zlib.gzipSync(data, { level: 9 })`（≙ gzip.BestCompression，:270；Node 产物不含 mtime，确定性成立），写 `src/Ts.Compiler/Diagnostics/loc/{tgtCul}.json.gz`（文件名 = 原始 TgtCul 如 `zh-CN.json.gz`，:255）。目录先清空重建（:193-198 同款）。
6. 计数断言：语言数 == expected-counts.json 的 `locales`（13）。
7. 发射 `Diagnostics/Loc.g.cs`：
   - 语言注册表（≙ loc_generated.go 的 matcher + localeFuncs，generate.go:298-312）：`static readonly string[] LocaleTags = { "cs-CZ", … };`（English 隐式 index 0 = 默认无翻译）。
   - **语言匹配**：Go 用 `golang.org/x/text/language.NewMatcher`（:298）。因 `InvariantGlobalization=true`（主计划 §三）**禁用 CultureInfo 匹配**，C# 手写针对这个 13+1 封闭集合的匹配：输入 BCP-47 标签 → 全标签大小写不敏感精确匹配 → 否则主语言子标签匹配取注册序第一个（"zh"→zh-CN）→ 否则 English。对照 x/text matcher 在此集合上的行为写表驱动单测；conformance 基线不行使 locale，风险登记 12-risks.md。
   - 惰性加载（≙ sync.OnceValue + go:embed，:315-335）：每语言一个 `Lazy<Dictionary<string,string>>`，从 `Assembly.GetManifestResourceStream("loc/cs-CZ.json.gz")` → GZipStream → STJ 源生成上下文反序列化 `Dictionary<string,string>`（07-aot-constraints.md 政策内）。
8. csproj 资源 glob（阶段 0 脚手架写死，不由生成器改写）：`<EmbeddedResource Include="Diagnostics/loc/*.json.gz" LogicalName="loc/%(Filename)%(Extension)" />`。

### 3.6 再生成与同步

`npm run gen:diagnostics`；需要 submodule 已检出（脚本启动时探测 `_submodules/TypeScript/src/compiler/diagnosticMessages.json`，缺失则报"运行 git submodule update --init"退出）。同步：submodule bump → 重跑 → 若计数变化先改 expected-counts.json → 审 diff（新消息、文本变更会体现在 Messages.g.cs 与 13 个 .gz 中）。`fast-xml-parser` 精确 pin 的原因：解析器升级导致的空白/实体处理差异不得使产物漂移。

---

## §4 libs 生成族 — `generate-libs.ts`

对应 `internal/bundled/generate.go`（218 行，`//go:generate go run generate.go`，bundled.go:14）。

### 4.1 输入与变换

1. `_submodules/TypeScript/src/lib/libs.json`（JSONC）：先跑 `stripJSONC` 的**同款正则**去注释与尾逗号（generate.go:130-157——原样移植两条正则，含"替换为空格保持偏移"的行为，虽然对 JSON.parse 无差异，仍照抄以便对照）。
2. 每个 lib：`sources = [name + ".d.ts"]`，`target = paths[name] ?? "lib." + name + ".d.ts"`（:179-186）；按 target 字符串序排序（:189-192，ordinal 序）。
3. 文件合成（:52-71）：`CopyrightNotice.txt`（CRLF→LF，:196-206）+ 对每个 source：`'\n'` + 源内容（CRLF→LF）。输出目录 `src/Ts.Compiler/Bundled/libs/` 先 RemoveAll 再重建（:44-49 同款——保证删除上游已移除的 lib）。
4. 计数断言：== expected-counts.json 的 `libs`（当前 **108**，与 tsgo `internal/bundled/libs` 实测一致）。

### 4.2 产物

- `Bundled/libs/*.d.ts` ×108（内容与 tsgo `internal/bundled/libs/*` **逐字节相同**——这是对照承重面：lib 文本参与 checker 行为与基线位置）。
- `Bundled/LibNames.g.cs`（≙ libs_generated.go，:74-88）：`public static readonly string[] LibNames = { "lib.d.ts", … };`（按名排序；载入顺序表在 tsoptions，注释同 Go）。
- Go 的 `embed_generated.go`（每文件一个 `//go:embed` 变量 + map + fs.DirEntry，:90-128）在 C# 由**清单资源机制天然替代**，不生成代码：csproj 写 `<EmbeddedResource Include="Bundled/libs/*.d.ts" LogicalName="bundled/libs/%(Filename)%(Extension)" />`（评审裁决 R12，与 07-aot-constraints.md §5.2 对齐）（`lib.dom.d.ts` 的 `%(Filename)`=`lib.dom.d`、`%(Extension)`=`.ts`，拼回原名——已验证 MSBuild 语义）；`bundled` 包的 VFS 覆盖层（`bundled:///libs/...`）移植时以 `LibNames` + `GetManifestResourceStream` 实现目录枚举与读取，行为对齐 Go 的 embeddedContents/libsEntries。
- Go 的 `!noembed` 构建变体（embed_generated.go 头部 build tag）**不复刻**：它服务 Go 侧开发期磁盘直读场景；C# 侧如需等价开发体验由 VFS 的测试构造完成，记录于 06-module-inventory.md 的 bundled 条目。

### 4.3 再生成与同步

`npm run gen:libs`；需 submodule。同步：submodule bump → 重跑 → `git status` 应只显示 lib 内容 diff / 新增删除；lib 集合变化时更新 expected-counts.json。**警告**：绝不手改 `Bundled/libs/` 下任何文件（该目录整体是生成物）。

---

## §5 LSP 元模型族 — `fetch-lsp-model.ts` + `generate-lsp.ts`

对应 `internal/lsp/lsproto/_generate/`：`fetchModel.mts`（40 行）+ `generate.mts`（3,437 行）→ `lsp_generated.go`（17,262 行）。阶段 8 交付，但脚本骨架与 fetch 在阶段 0 一并建好（空壳政策见 §1.2）。

### 5.1 metaModel 获取方式（与上游的一处刻意偏差）

上游流程：`fetchModel.mts` 从 `package-lock.json` 读 `vscode-languageclient` 版本（当前 **10.0.1**，fetchModel.mts:16-18）→ 拉取 `vscode-languageserver-node@release/client/{ver}` 的 `protocol/metaModel.json` 与 `tools/src/metaModel.ts`（:23-24），后者打补丁加 `omitzeroValue`（:33-37）；`metaModel.json` 被 **gitignore**（`internal/lsp/lsproto/_generate/.gitignore`），每次生成前须联网 fetch。

我们的决定：**`metaModel.json` 拷贝入仓**（`tools/generator/metaModel.json`），`lsp-model-version.json` 记录 pin（因为 C# 仓没有 `vscode-languageclient` 依赖可供锁文件推导）。理由：

1. CI 新鲜度检查（§9.4）必须离线可重跑——上游的"fetch-on-demand"会把 GitHub raw 可用性变成 CI 依赖；
2. 该文件是纯数据、变更频率极低、约 700KB，入仓成本可忽略；
3. 上游 gitignore 的动机（避免与 lockfile 版本脱钩）由我们的显式 `lsp-model-version.json` + fetch 脚本内断言（拉取后对比版本、覆写副本）等价达成。

`fetch-lsp-model.ts` = fetchModel.mts 直译：读 `lsp-model-version.json` → 拉取两文件 → 对 metaModel schema 打同款 `omitzeroValue` 补丁 → 覆写入仓副本。仅在升级 LSP 协议 pin 时手动运行（`gen:lsp:fetch`）。**注意**：升级 LSP pin 必须与 tsgo pin 联动（tsgo 的 lsp_generated.go 由其 lockfile 版本决定；若两边 metaModel 版本不一致，LSP 线协议对照测试会假阳性）——SOP 见 §10。

### 5.2 模型预处理：100% 原样保留

`generate.mts` 的前半（:37-1339）是与目标语言无关的模型修补，**逐行直译**，不做任何"简化"：

- 自定义 structures/enumerations/requests/notifications/typeAliases（:37-864：InitializationOptions、AutoImportFix、CompletionItemData、VS 系列、Telemetry 系列、custom/* 方法等）；
- `patchAndPreprocessModel`（:880-1304）：ServerCapabilities/HoverParams/Hover/ClientCapabilities/SignatureInformation 补丁（:937-1017）、`initializationOptions`/`data`/`ProgressParams.value` 的 LSPAny 替换（:1023-1061）、registrationOptions 的 "and" 合成结构（:894-931）、**继承内联展平**（:1096-1131，属性同名以本结构覆盖）、`_InitializeParams` 移除（:1159）、**notebook 全族剔除**（:1162-1246）、registrationMethods 表（:1249-1280）、LSPErrorCodes 并入 ErrorCodes（:1283-1290）、枚举单数化（Codes/Modifiers/Types → 去 s，:1293-1303）；
- telemetry 校验（:1311-1339）。

### 5.3 类型解析与命名：保留 Go 命名结果

`resolveType`（:1371-1484）的 C# 基类型映射：integer→`int`（Go int32）、uinteger→`uint`、string→`string`、boolean→`bool`、decimal→`double`、`URI`/`DocumentUri`→同名包装类型、null→（联合类型内处理）、tuple [uinteger,uinteger]→`readonly record struct`（≙ `[2]uint32`，:1441-1451）、stringLiteral/integerLiteral/booleanLiteral→单值哨兵类型（≙ :1453-1468 的 literalTypes）。

**union 类型命名算法逐字保真**（flattenOrTypes :1486-1512、成员命名与 pluralize :1514-1577、PascalCase 分块最长公共前缀提取 :1580-1642、`OrNull` 后缀 :1644-1649）——产物类型名如 `LocationOrLocationsOrDefinitionLinksOrNull` 必须与 Go 相同（grep 对照 + 方法签名对照）。`valueTypes` 集合（Position/Range/Location 等 9 个，:1686-1696）→ C# `readonly record struct`（顺带自然获得 `compareStructures`（:845-849）要求的值比较；`Compare` 方法仍按声明序字段比较显式生成）。`goFieldName` 的 `_vs_` → `VS` 前缀规则（:1364-1369）与 `methodNameIdentifier`（:1753-1760）照搬。

### 5.4 OR 类型的手写 JsonConverter 策略

Go 形态（lsp_generated.go:11566 起的 `IntegerOrString` 等）：每 union 一个 struct、每变体一个指针字段、`MarshalJSONTo` 写首个非 nil（共享 `marshalUnion` 帮助函数）、`UnmarshalJSONFrom` 按三级分派。C# 形态：

```csharp
[JsonConverter(typeof(IntegerOrStringConverter))]
public sealed class IntegerOrString
{
    public int? Integer { get; set; }
    public string? String { get; set; }
}
```

- "手写 Converter"（主计划 §一.4 用语）的准确含义：**不依赖 STJ 反射/默认多态，每个 union 由 generate-lsp.ts 发射一个具体 `JsonConverter<T>` 子类**，挂 `[JsonConverter]` 特性（STJ 源生成尊重该特性，AOT 安全）。共享的底层帮助函数（≙ Go 的 `marshalUnion`、`jsonObjectRawField`、`jsonObjectHasKey`，手写于 `Lsp/UnionSerialization.cs`）是真正手写的部分。
- `Write`：按声明序写第一个非 null 变体；全 null 时若 union 含 Null 变体（`…OrNull`）写 `null`，否则抛（对齐 Go marshalUnion 语义——移植时以 structcodec.go/util.go 中 marshalUnion 实现为准）。
- `Read` 三级分派，逐级复刻 generate.mts 的决策树：
  1. **JSON kind switch**（≙ :1766-1843 `jsonKindForType` + PeekKind switch）：`Utf8JsonReader.TokenType`（Number/String/StartObject/StartArray/True/False/Null）可唯一定变体时直接反序列化；
  2. **字符串字面量判别字段**（≙ findDiscriminatorField :1885-1938 + generateDiscriminatorDispatch :2008-2025）：对象变体间以某属性的 stringLiteral 值区分——用 `reader` 的**结构体拷贝**向前扫描该属性值（等价 Go 的原始字节扫描，不整树解析），switch 后反序列化；
  3. **presence 键分派**（≙ findPresenceDiscriminator :1946-1987 + 扁平收集 :2067-2124）：以"某必需属性只在一个变体出现"分派；剩余 >1 时退化为 try-each（逐个尝试反序列化，成功即取——性能敏感度低的兜底，Go 同款 :2052-2059）。
  - 生成器**在生成期**完成全部分派决策（哪级、哪个字段、哪些键），产物里只有平铺的 switch——与 Go 产物同构，便于逐 union 对照审查。
- 必需字段校验：Go 用 `lsp:"required"` tag + 生成的 `UnmarshalJSONFrom`（structcodec.go 驱动）。C#：结构属性用 C# `required` 修饰符 → STJ（.NET 8+，源生成路径）在缺失时抛 `JsonException`；`omitzero`/optional → 可空属性 + `[JsonIgnore(Condition = WhenWritingNull)]`；`omitzeroValue`（非指针 omitzero，metaModel 补丁引入）→ 值类型默认值时忽略（`WhenWritingDefault`）。逐属性语义以 Go struct tag 为准生成（lsp_generated.go 中 `json:"…,omitzero"` 的出现即依据）。

### 5.5 STJ 源生成上下文组织

`Lsp/Protocol.g.cs` 尾部发射唯一上下文（07-aot-constraints.md：反射序列化器封禁）：

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,   // 属性 PascalCase→线上 camelCase；_vs_/$ 前缀等
                                                              // 不规则名逐属性 [JsonPropertyName] 覆盖
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]      // 忽略策略逐属性显式发射，不用全局默认
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
// … 生成器为每个「请求 params/result、通知 params、union、枚举载体」各发射一条 JsonSerializable
internal sealed partial class LspJsonContext : JsonSerializerContext { }
```

- 生成器从模型**收集可达类型闭包**发射 `[JsonSerializable]` 清单（顶层消息类型 + 全部 union + string 枚举包装），确保 AOT 下无运行时元数据缺口。
- Method 常量、`RequestInfo<TParams>`/`NotificationInfo<TParams>` 注册表、`UnmarshalParams` 分派 switch（lsp_generated.go:10975、:11525、:5173 形态）逐一直译。
- 整数枚举 → C# enum（值序列化）；字符串"枚举"（Go `type X string` + 常量）→ `readonly record struct X(string Value)` + 静态成员 + 生成的小 converter——不用 `JsonStringEnumConverter`（其命名策略不可控且 AOT 需泛型特化，直接生成精确字符串更稳）。
- 产物单文件 `Protocol.g.cs`（主计划表定名）；若移植时体量（预估 >20k 行）造成 IDE 顿挫，按主计划巨文件拆分惯例分 partial（Protocol.g.cs / Protocol.Unions.g.cs / Protocol.Json.g.cs），记 §11。

### 5.6 再生成与同步

`npm run gen:lsp`（离线，读入仓副本）。同步分两种：(a) tsgo pin 前进且其 `generate.mts` 变化 → 人工 diff 移植（该文件是 7 个脚本中最大的移植面，3,437 行，阶段 8 的第一件事）；(b) LSP 协议版本升级 → 改 `lsp-model-version.json` → `gen:lsp:fetch` → `gen:lsp`。

---

## §6 Unicode 表族 — `transcribe-unicode.ts`（一次性转写）

对应 `internal/stringutil/_scripts/generate-unicode-data.mts`（248 行；`//go:generate` 绑定于 stringutil/generate.go:3-4）。上游从 pin 死的 `@unicode/unicode-15.1.0` 包（generate-unicode-data.mts:11-12）生成两个 Go 文件：`js_case_generated.go`（3,496 行：specialCasingMappings 映射 + Cased/Case_Ignorable RangeTable）与 `identifier_parts_generated.go`（1,391 行：ID_Start / ID_Start∪ID_Continue 两张 RangeTable）。

### 6.1 决定：从 Go 生成表转写，而非从 @unicode 包重新生成

**决定**：写一次性脚本 `transcribe-unicode.ts`，**解析 tsgo 的两个已生成 Go 文件**（文本解析：两文件均为机器产出、格式刚性），发射等价 C#。不引入 `@unicode/unicode-15.1.0` 依赖、不移植 `toStrideRanges`/`toRangeTable` 压缩算法。

理由：

1. **数据 provenance 与 tsgo 完全一致**：转写是纯格式变换，正确性验证退化为"解析回读 == 原文数据"的机械断言；若重新生成，则必须额外证明我们的 stride-range 压缩（generate-unicode-data.mts:60-94，含 latinOffset 计算 :89-93）与上游 range-for-range 相同才能对照——平添一整层证明义务，收益为零。
2. **主计划 §一.4 表已定输入为"Go 生成表"**，本决定与之一致。
3. 上游 bump UNICODE_VERSION 的频率极低（pin 在脚本常量 + devDependency，:11）；届时重跑转写即可（输入换成新的 Go 生成表）。
4. 诚实记录缺点：文本解析 Go 源对格式变化脆弱——但输入本身是同一脚本的机器产物，格式漂移必然伴随上游脚本 diff（§10 同步时可见），风险可控；解析失败即报错，不会静默产出坏表。

### 6.2 产物形态

- `StringUtil/IdentifierParts.g.cs`：两张表，各拆为 BMP 与增补两段（Go RangeTable 的 R16/R32 结构照搬）：

```csharp
internal static partial class UnicodeTables
{
    // {lo, hi, stride} 三元组平铺；ReadOnlySpan<ushort> over 常量数组 ⇒ RVA 静态数据，零分配零 cctor
    internal static ReadOnlySpan<ushort> IdentifierStartR16 => new ushort[] { 0x41, 0x5A, 1, /*…*/ };
    internal static ReadOnlySpan<uint>   IdentifierStartR32 => new uint[]   { 0x10000, /*…*/ };
    internal const int IdentifierStartLatinOffset = 6;   // 逐表照抄 Go LatinOffset
    // IdentifierPart 同构 ×1
}
```

- `StringUtil/JsCase.g.cs`：Cased/CaseIgnorable 两张 RangeTable（同上形态）+ specialCasingMappings：Go `map[rune]specialCasingMapping`（js_case_generated.go）→ **按码点升序的平行常量数组 + 二分查找**（键 `ReadOnlySpan<uint>`、lower/upper/conditionalLower 字符串数组、condition 字节数组）——AOT 零反射、无字典分配；`FrozenDictionary` 亦可但无必要。
- 查询算法（`unicode.Is` 等价：LatinOffset 线性快路径 + 其余二分、stride 判定 `(cp - lo) % stride == 0`）**手写**在 `StringUtil/Unicode.cs`（阶段 1 stringutil 移植的一部分，非生成物）；生成物只含数据。
- 验证：转写脚本内置回读断言（发射前把解析出的数据重排成 Go 文本形状与原文比对）；stringutil 单测（Go 侧 `js_case_test` 等移植件）+ 阶段 2 全语料 token 流 diff 是最终 oracle（标识符边界直接受这两张表支配）。

### 6.3 再生成

`npm run gen:unicode`——**不在 gen:all**，产物入库后视为静态；仅当上游 bump Unicode 版本（表现为两个 Go 生成文件 diff）时重跑。脚本输入路径指向 tsgo 仓的绝对/相对路径由 CLI 参数给出：`npm run gen:unicode -- --tsgo /path/to/typescript-go`（这也是全管线中唯一需要 tsgo 仓在场的脚本，因此才被排除出 CI 新鲜度检查）。

---

## §7 枚举 stringer 族 — `generate-stringers.ts`

Go 用 `x/tools/cmd/stringer` 为 11 个枚举生成 `String()`（`grep -rn go:generate` 全量清单，计数经评审裁决 R11 核定）：ast.Kind（kind_generated.go:5）、checker.SignatureKind（types.go:14）、core.ModuleKind/ScriptTarget（compileroptions.go:12-13，`-trimprefix`）、core.LanguageVariant（languagevariant.go:3）、core.ScriptKind（scriptkind.go:3）、core.Tristate（tristate.go:3）、diagnostics.Category（diagnostics.go:17）、autoimport.ExportSyntax（export.go:11）、project.Kind（project.go:27，trimprefix）、vfsmatch.Usage（vfsmatch.go:15，trimprefix）。

C# **不整族照搬**：枚举的 `ToString()` 在 AOT 白名单内（主计划 §一.5"禁反射（typeof/枚举 ToString 除外）"）足够覆盖调试用途。只为**输出承重**的枚举生成显式 `ToDisplayString()`。

### 7.1 承重判据（按序检验，命中其一即生成）

某枚举的 Go `String()` 输出能到达：

1. **conformance 基线字节**——作为诊断消息参数、`--showConfig`/trace 输出、fileInclude 解释等进入 errors.txt/js/types 基线；
2. **差分夹具格式**——dumptool 转储（token 流/AST/symbol）打印的枚举名，Go/C# 两侧必须产出相同字符串；
3. **线协议或持久化格式**——LSP/API/.tsbuildinfo 中出现。

不命中者一律不生成：调试/日志/panic 消息走 C# `ToString()`（注意：panic 消息文本本身不进基线，但为了移植对照的舒适度，生成器对**已生成 ToDisplayString 的枚举**在 panic 直译处也用它，见 §2.2 #8）。

### 7.2 初始清单（源码取证）

| 枚举 | 判定 | 证据 | Go 输出形态 |
|---|---|---|---|
| `ast.Kind`（C# `SyntaxKind`） | **生成**（判据 2） | 阶段 2/3 门禁的 token/AST 转储夹具打印 kind 名；Go 侧 dumptool 用 `Kind.String()`；另见生成产物中的 panic 文本（generate-go-ast.ts:406、:635）与 checker panic（checker.go:6976、:15679） | **无 trimprefix** ⇒ `"KindIdentifier"` 带 Kind 前缀——C# `SyntaxKind.Identifier.ToString()` 给 `"Identifier"`，**不等**，必须生成 |
| `core.ModuleKind` | **生成**（判据 1） | `c.moduleKind.String()` 作为诊断参数三处：checker.go:5302（TS1543）、:10589（TS2725）、:14654（TS1544）→ errors.txt 字节 | trimprefix=ModuleKind ⇒ `"NodeNext"`、`"ES2015"` 等；C# 成员名若按惯例重命名（`Es2015`?）ToString 即漂移 ⇒ 生成锁死 Go 字符串 |
| `core.ScriptTarget` | **生成**（判据 1） | `GetEmitScriptTarget().String()` → `diagnostics.Default_library_for_target_0`：fileInclude.go:210、:291 → errors.txt 字节 | trimprefix=ScriptTarget ⇒ `"ES2020"` 等 |
| `diagnostics.Category` | **手工移植，非本族** | errors.txt 的 `"!!! %s TS%d"` 用的是**手写** `Name()`（error_baseline.go:104 → diagnostics.go:29-41 返回小写 "error" 等）；stringer 的 `String()`（"CategoryError"）仅调试 | Name() 随 Diagnostics.cs 手写；stringer 不生成 |
| SignatureKind / LanguageVariant / ScriptKind / Tristate / project.Kind / vfsmatch.Usage / ExportSyntax | **暂不生成** | 逐一 grep 无基线/夹具/协议路径（仅调试与测试断言消息） | 各包移植时复核；发现新泄漏路径 → 加入 stringers.json 并在 11-progress.md 记录 |

### 7.3 实现

- 声明清单 `tools/generator/stringers.json`：每项 `{ "enum": "Ts.Ast.SyntaxKind", "source": "ast.json" }` 或 `{ "enum": "Ts.Core.ModuleKind", "goPrefix": "ModuleKind", "trimPrefix": true, "members": [["None",0],["CommonJS",1],…] }`——AST Kind 的成员表从 ast.json 推导（与 gen:ast 同源）；其余枚举成员表**内联在 stringers.json**（数量小、变更少），与手写 C# 枚举的同步靠单测：`Enum.GetValues<T>()` 与生成表逐项对照（测试项目不 AOT，反射可用）。
- 产物：宿主项目内 `XxxStringers.g.cs`，形态为 switch 表达式扩展方法：

```csharp
public static class SyntaxKindExtensions
{
    public static string ToDisplayString(this SyntaxKind kind) => kind switch
    {
        SyntaxKind.Unknown => "KindUnknown",
        SyntaxKind.EndOfFile => "KindEndOfFile",
        // …；marker 成员（FirstAssignment 等重复值）不发射 case（与 Go stringer 对重复值的处理一致：只认首个定义名）
        _ => $"SyntaxKind({(short)kind})",   // ≙ Go stringer 的 "Kind(n)" 越界回退，格式按 Go 版式
    };
}
```

- 越界回退文本必须复刻 Go stringer 的 `"Kind(" + strconv.FormatInt(...) + ")"` 版式（kind_stringer_generated.go 尾部逻辑），夹具中可能出现。

再生成：`npm run gen:stringers`（在 gen:all 内，因依赖 ast.json）。

---

## §8 API 二进制编码器族 — `generate-csharp-encoder.ts`（阶段 9）

对应 `_scripts/generate-encoder.ts`（2,041 行），上游产物三件（generate-encoder.ts:7-11）：`internal/api/encoder/encoder_generated.go`（707 行）、`decoder_generated.go`、npm 包的 `protocol.generated.ts`。

- 输入：同一份 `tools/generator/ast.json`（与 §2 共享副本——**编码器对 schema 的视角不同**：成员过滤是 `!noTS && !noGo`（generate-encoder.ts:60-62），而 AST 生成是 `!noFactory`（generate-go-ast.ts:38-40），移植时勿混用）。
- C# 产物：`src/Ts.LanguageService/Api/Encoder.g.cs` + `Api/Decoder.g.cs`（主计划表写单条 `Api/Encoder.g.cs`，按 Go 双文件对齐拆二，记 §11）。生成面直译：`GetNodeDataType`（:366）、`GetChildrenPropertyMask`（:409）、`GetNodeCommonData`（含 bool/SyntaxKind-union 的位打包，:459 与 :95-120 的分类函数）、`RecordNodeStrings`（:525）、`RecordExtendedData`（:556）、decoder 的 `CreateStringNode`/`CreateExtendedNode`/`CreateChildrenNode`（:611/:674/:702）。
- **TS 侧 `protocol.generated.ts` 不生成**：它服务 `_packages/native-preview` 客户端（不在移植范围）。但**线格式必须与 tsgo 逐字节一致**（官方 TS 客户端应可直连 C# 服务端）——阶段 9 门禁"api 编码器往返测试"外，增加与 Go 侧编码产物的逐字节对照夹具（dumptool 增补一个 encode 导出命令，规格入 08-testing-strategy.md——08 已增补该族规格，该文档正在补写中，以其增补后版本为准）。
- 再生成：`npm run gen:encoder`（gen:all 内；阶段 9 前为空壳）。同步随 ast.json（§2.5）。

---

## §9 生成物通用规范

### 9.1 文件头标记

每个 `.g.cs` 首行块统一为（由 `csharp-emit.ts` 的 `autoGeneratedHeader` 发射）：

```csharp
// <auto-generated>
//     Code generated by tools/generator/generate-csharp-ast.ts. DO NOT EDIT.
//     Inputs: tools/generator/ast.json
// </auto-generated>
#nullable enable
```

- `<auto-generated>` 标准标记：Roslyn analyzer/StyleCop 自动豁免风格规则、IDE 提示只读。
- **必须显式 `#nullable enable`**：含 `<auto-generated>` 头的文件默认脱离项目的可空注解上下文，不加此行则所有 `?` 注解静默失效——这是 C# 生成代码的经典陷阱。
- **不嵌入 commit hash / 时间戳 / 工具版本号**——保证等幂再生成（§0 铁律 5），pin 信息统一记录在 00-overview.md 的钉扎章节与 `lsp-model-version.json` 等数据文件。
- 生成的资源类产物（`.d.ts`、`.json.gz`）不加头（字节保真面）。

### 9.2 格式化政策：生成器发射即最终格式，`dotnet format` 不参与

- 上游依赖后置格式化器（Go：`go/format` + `dprint`，见 generate.go:111、generate-go-ast.ts:1010、generate.mts 末尾 `dprint fmt`）。我们**不引入等价物**：`dotnet format` 的输出受 SDK 版本影响，会把"生成物新鲜度"耦合到第二个工具的版本行为上；生成器自己发射规范格式（4 空格缩进、Allman 花括号、文件尾单换行——与仓库 .editorconfig 一致）成本很低且完全确定。
- `.editorconfig` 增加 `[*.g.cs] generated_code = true`；CI 的 `dotnet format --verify-no-changes` 步骤（若阶段 0 启用）**排除** `**/*.g.cs`。
- `Bundled/libs/**`、`Diagnostics/loc/**` 同理排除出一切格式化。

### 9.3 AOT 与分析器

生成物与手写代码同权受 `IsAotCompatible` + AOT/Trim analyzer 约束（Directory.Build.props 全局开启，主计划 §三）；阶段 0 门禁明确含"Ast/*.g.cs 过 AOT+Trim analyzer"。生成器发射的代码模式已按 07-aot-constraints.md 预筛：无反射、无 `Type.GetType`、STJ 仅上下文、枚举 ToString 仅调试路径。

### 9.4 CI「生成物新鲜度」检查

独立 CI job（每 PR 必跑；门禁归属详见 08-testing-strategy.md）：

```yaml
freshness:
  steps:
    - checkout（含 submodule: _submodules/TypeScript，深度完整——.lcl 与 src/lib 在其中）
    - setup-node（版本读 tools/generator/package.json engines；用 .nvmrc 固定）
    - run: npm ci --prefix tools/generator
    - run: npm run --prefix tools/generator gen:all
    - run: git diff --exit-code
    - run: git status --porcelain | (! grep .)   # 捕获新增/删除文件（RemoveAll 型生成器会删陈旧产物）
```

- 语义：**重新生成后 git diff 必须为空**——任何人手改 `.g.cs`、或改了生成器/输入却忘了重跑、或生成器不等幂，都在此失败。
- `gen:all` 的排除项与其理由：`gen:unicode`（需要 tsgo 仓在场，产物静态）、`gen:lsp:fetch`（需网络；metaModel.json 已入仓故 `gen:lsp` 本身离线可跑）。
- Node 版本 pin（.nvmrc + engines）是新鲜度检查稳定性的一部分：脚本避免使用任何随 Node 版本漂移的输出（如 `Intl`、对象键枚举顺序依赖——全部显式排序）。

---

## §10 上游同步 SOP（tsgo pin 升级时）

主计划钉扎策略（00-overview.md）规定 submodule commit 与 tsgo pin 一致。每次 pin 前进，代码生成侧执行：

1. **资产 diff**：对下表逐项 `diff` 上游与本仓副本，逐字节更新副本：
   `_scripts/ast.json`、`_scripts/ast.schema.json`、`_scripts/schema.ts`、`internal/diagnostics/extraDiagnosticMessages.json`、（若 LSP pin 变动）metaModel 相关。
2. **生成器 diff**：对 `_scripts/generate-go-ast.ts`、`_scripts/generate-encoder.ts`、`internal/diagnostics/generate.go`、`internal/bundled/generate.go`、`internal/lsp/lsproto/_generate/generate.mts`、`internal/stringutil/_scripts/generate-unicode-data.mts` 查上游变更；有变更者将语义同步移植到对应 C# 生成脚本（这是"移植生成器"路线的持续成本，换来的是产物永不手抄）。
3. **计数更新**：上游消息/lib/语言/节点数变化 → 先改 `tools/generator/expected-counts.json`（diff 里显式可见）。
4. **重生成**：`npm run gen:all`（+按需 `gen:unicode`）。
5. **审查产物 diff**：生成物 diff 应与上游 release note/commit 可解释对应；无法解释的 diff 一律先查生成器移植错误。
6. **回归**：跑当前已达阶段的全部门禁套件（新鲜度 job 自动覆盖第 4 步的遗漏）。

Go 侧 stringer（`x/tools/cmd/stringer`）无需同步动作——我们不消费其产物，只在 §7 清单变化时更新 stringers.json。

---

## §11 待澄清（不改设计，仅列疑点）

1. **AST 产物文件数——已关闭（评审裁决 R2）**：定稿 7 个物理文件（Nodes/NodeCasts/NodeFactory/Visitors/Accessors/SyntaxKind/GlobalUsings，§2.4），与 02-ast-design.md §16 同文；主计划"~5 个"是约数，以 7 为准。`GlobalUsings.g.cs` 的跨项目 `<Compile Link>` 共享机制照 §2.2 #5 执行。
2. **metaModel.json 入仓**与上游 gitignore 政策相反（§5.1 给出理由：CI 离线新鲜度检查）。若 00-overview.md 的钉扎策略希望与上游文件布局零偏差，需要另行给 CI 提供网络豁免方案。
3. **Encoder 产物文件数**：主计划表写 `Api/Encoder.g.cs` 单条；Go 侧实际为 encoder+decoder 两文件（generate-encoder.ts:8-9），本文按两文件规划（§8）。
4. **`Protocol.g.cs` 体量**：Go 产物 17,262 行，C# 预计更大；主计划巨文件拆分惯例（">15k 行按分节拆 partial"）是否适用于生成物待定（§5.5）。
5. **Is*/包级函数宿主静态类名——已关闭（评审裁决 R7）**：定稿 `Ts.Ast.NodeTests`（对应原版 TypeScript 的 factory/nodeTests.ts，域忠实），原 `Ts.Ast.AstFacts` 占位废弃；§2.2 #14 已更新，生成器以配置常量引用该定稿名，`gen:ast` 可据此冻结。
6. **stringers.json 内联成员表**（§7.3）与 06-module-inventory.md 中各枚举宿主包的"代码生成参与"列需要互相登记（ModuleKind/ScriptTarget 定义在 core 包手写文件中，生成物却依赖其成员值——单测对照可兜底，但清单要一致）。
7. **语言匹配器**（§3.5-7）：InvariantGlobalization 下手写 13+1 标签匹配对 x/text `language.Matcher` 的行为对齐只做了表驱动近似；若阶段 9 的 locale 相关行为进入某类对照测试，需要升级为逐案验证（现登记 12-risks.md）。

---

*本文引用的全部行号基于 tsgo 仓当前工作副本（与主计划同一 pin）。任何行号漂移以 §10 同步流程为准修订。*
