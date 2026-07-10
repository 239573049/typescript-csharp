# 06 · 模块清单：internal/ 全部包的逐一移植档案

| 元信息 | |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-07-11 |
| 状态 | Draft |
| 上游快照 | /Users/token/Desktop/code/typescript-go-main（与 _submodules/TypeScript 钉扎同一 commit，见 00-overview.md） |
| 交叉引用 | 00-overview.md（钉扎策略）· 01-architecture.md（项目/线程/JSON 字节保真）· 02-ast-design.md（AST 生成面）· 03-checker-design.md（Type 层次/缓存）· 04-codegen.md（七族生成器）· 05-conventions.md（惯用法圣经）· 07-aot-constraints.md（禁反射政策）· 08-testing-strategy.md（差分夹具/conformance）· 09-phases.md（门禁展开，补写中）· 11-progress.md（活状态表）· 12-risks.md（风险册） |

---

## 0. 使命、口径与读法

本文档是移植工程师的**逐包作业档案**：每个 Go 包一节，回答"这个包移到哪、什么时候移、按什么文件对文件的映射移、哪些测试跟着走、哪里会咬人、先要移完谁"。它是 11-progress.md 状态跟踪的初始快照，也是主计划 §一.3 要求的"巨文件拆分表"的存放处（见 §5.1 checker）。

**LOC 口径**：非测试 Go 行数（`find internal/<pkg> -name '*.go' ! -name '*_test.go' | xargs wc -l`），含入库的生成物（`*_generated.go`）。45 个列名包合计 **297,948** 行；加上未列入清单的 3 个微包（debug 61 / locale 28 / nodebuilder 78，见 §12）为 298,115 行；与主计划总数 300,749 的差额 ≈2,600 行是 `cmd/tsgo/` 入口与仓库根杂项（§11.4）。测试 LOC 单独标注（同法统计 `*_test.go`）。

**每节字段**：
- **映射**：Go 路径 → C# 项目 / 文件夹 / 命名空间。命名空间 = `Ts.` + 包名 PascalCase（`internal/ast`→`Ts.Ast`）；子包 = 嵌套命名空间（`vfs/osvfs`→`Ts.Vfs.OsVfs`）。文件 1:1：`scanner.go`→`Scanner.cs`（05-conventions.md 命名规则）。
- **阶段**：主计划 §二 的 0–10 阶段归属。跨阶段拆移的包（compiler、fswatch、tracing）分行标注。
- **代码生成**：是否参与 04-codegen.md 的七族生成器；Go 侧 `*_generated.go` 在 C# 侧的对应产物。
- **测试资产**：需移植的 `*_test.go` 清单与目标测试项目。**Go 单测原则上全部移植**（08-testing-strategy.md），本列只标注例外与吨位。
- **危险点**：反射 / unsafe / OS 特定 / 深递归 / map 迭代序 / 字节保真等，均给出 file:line 证据。
- **依赖**：该包生产代码 import 的其他 internal 包（据非测试文件 import 实测；子包级修正见 §13.1）。
- **初始状态**：本文档定稿时一律「未开始」；此列此后**只在 11-progress.md 维护**，本表不再更新。

---

## 1. 按阶段分组的汇总表

> 项目列缩写：C = src/Ts.Compiler，LS = src/Ts.LanguageService，CLI = src/Ts.Cli，TU = tests/Ts.TestUtil，CT = tests/Ts.Conformance.Tests，FT = tests/Ts.Fourslash.Tests。

### 阶段 0 · 脚手架（生成器自举）

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| bundled | 1,110 | C | Ts.Bundled | libs 拷贝脚本 + EmbeddedResource glob | 未开始 |
| diagnostics | 9,423 | C | Ts.Diagnostics | generate-diagnostics.ts → Messages.g.cs（2,154 条） | 未开始 |
| *(ast 生成器自举也在阶段 0，包本体归阶段 3)* | | | | generate-csharp-ast.ts → Ast/*.g.cs ×7（R2，02-ast-design.md §16 定稿） | |

### 阶段 1 · 地基

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| core | 2,669 | C | Ts.Core | stringer ×5 | 未开始 |
| collections | 840 | C | Ts.Collections | — | 未开始 |
| tspath | 1,435 | C | Ts.TsPath | — | 未开始 |
| vfs | 3,131 | C（测试子包→TU） | Ts.Vfs.* | vfsmock 手写化 | 未开始 |
| nativepath | 298 | C | Ts.NativePath | — | 未开始 |
| jsnum | 584 | C | Ts.JsNum | — | 未开始 |
| stringutil | 5,573 | C | Ts.StringUtil | Unicode 表转写 | 未开始 |
| glob | 349 | C | Ts.Glob | — | 未开始 |
| semver | 712 | C | Ts.Semver | — | 未开始 |
| debug（微包，§11.1：吸收为 Core/Debug.cs，不占 41 包名额，R3） | 61 | C | Ts.Core（Debug 静态类） | — | 未开始 |
| **json（§10.1：薄封装消失，不移植）** | 100 | — | —（调用点直用 STJ） | — | N/A |
| **repo（§10.3：测试/生成器专用）** | 87 | TU | Ts.TestUtil | — | 未开始 |

### 阶段 2 · Scanner

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| scanner | 4,256 | C | Ts.Scanner | Unicode 属性表转写 | 未开始 |

### 阶段 3 · AST + Parser + 测试基架

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| ast | 20,847 | C | Ts.Ast | Ast/*.g.cs ×7（含 SyntaxKind.g.cs）+ stringer | 未开始 |
| parser | 9,071 | C | Ts.Parser | — | 未开始 |
| astnav | 783 | C | Ts.AstNav | — | 未开始 |
| testrunner | 889 | CT | Ts.TestRunner | — | 未开始 |
| testutil | 5,730 | TU | Ts.TestUtil.* | mock 手写化 | 未开始（滚动至阶段 8） |

### 阶段 4 · Binder + 选项 + 程序骨架

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| binder | 3,555 | C | Ts.Binder | — | 未开始 |
| tsoptions | 6,144 | C | Ts.TsOptions | 反射 merge → per-field switch 生成 | 未开始 |
| module | 2,780 | C | Ts.Module | — | 未开始 |
| packagejson | 662 | C | Ts.PackageJson | — | 未开始 |
| symlinks | 134 | C | Ts.Symlinks | — | 未开始 |
| outputpaths | 307 | C | Ts.OutputPaths | — | 未开始 |
| diagnosticwriter | 506 | C | Ts.DiagnosticWriter | — | 未开始 |
| compiler（子集：fileloader/Program） | 5,658 | C | Ts.Compiler | — | 未开始 |

### 阶段 5 · Checker（主峰）

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| checker | 59,822 | C | Ts.Checker | stringer | 未开始 |
| evaluator | 168 | C | Ts.Evaluator | — | 未开始 |
| nodebuilder（微包，§11.3） | 78 | C | Ts.NodeBuilder | — | 未开始 |
| pseudochecker | 1,126 | C | Ts.PseudoChecker | — | 未开始 |
| modulespecifiers | 2,280 | C | Ts.ModuleSpecifiers | — | 未开始 |
| tracing（仅接口骨架，本体归阶段 9） | (763) | C | Ts.Tracing | — | 未开始 |

### 阶段 6 · Emit

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| printer | 11,442 | C | Ts.Printer | — | 未开始 |
| transformers | 24,323 | C | Ts.Transformers.* | — | 未开始 |
| sourcemap | 1,013 | C | Ts.SourceMap | — | 未开始 |
| compiler（补齐 emitter.go） | — | C | Ts.Compiler | — | 未开始 |

### 阶段 7 · tsc CLI

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| execute | 9,348 | CLI（归属待澄清 §13.2） | Ts.Execute.* | buildinfo 选项遍历 → 生成 switch | 未开始 |
| pprof（§10.2：EventPipe，非 1:1） | 169 | CLI | Ts.Profiling | — | 未开始 |
| fswatch（最小后端提前，本体归阶段 8，§13.3） | (5,446) | LS | Ts.FsWatch | — | 未开始 |

### 阶段 8 · 语言服务

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| ls | 38,933 | LS | Ts.Ls.* | stringer | 未开始 |
| format | 4,291 | LS | Ts.Format | — | 未开始 |
| lsp | 21,008 | LS | Ts.Lsp.* | generate-lsp.ts → Protocol.g.cs | 未开始 |
| project | 11,016 | LS | Ts.Project.* | stringer | 未开始 |
| fswatch | 5,446 | LS | Ts.FsWatch | — | 未开始（FileSystemWatcher 重写，非 1:1） |
| jsonrpc | 287 | LS | Ts.JsonRpc | — | 未开始 |
| fourslash（测试基架） | 9,770 | FT | Ts.Fourslash | convertFourslash.mts 改造为发射 C# | 未开始 |

### 阶段 9 · API + 长尾

| Go 包 | LOC | 项目 | 命名空间 | 代码生成 | 状态 |
|---|---:|---|---|---|---|
| api | 9,110 | LS | Ts.Api.* | generate-encoder.ts → Encoder.g.cs | 未开始 |
| tracing（§9.2：EventSource，非 1:1） | 763 | C | Ts.Tracing | — | 未开始 |
| locale（微包，§11.2） | 28 | C | Ts.Locale | — | 未开始 |

阶段 10（加固）无新包。以下各节按上述阶段顺序展开。

---

## 2. 阶段 0/1 · 地基包

### 2.1 internal/bundled → Ts.Compiler / Bundled/ / Ts.Bundled

- **LOC** 1,110（含生成物）；测试 48。**阶段 0**。
- **职责**：内嵌 108 个 `lib.*.d.ts`（`bundled/libs/`，实测 108 个文件）并以 `bundled:///` scheme 覆盖 VFS（bundled.go、embed.go）。
- **文件映射**：

| Go | C# | 说明 |
|---|---|---|
| bundled.go | Bundled.cs | `Embedded` 常量、`LibPath()`、VFS 包装入口 |
| embed.go | EmbeddedFs.cs | `//go:embed` 读取层 → `Assembly.GetManifestResourceStream` |
| embed_generated.go（452 行） | —（消失） | 108 条 `//go:embed` 声明 → csproj 中 `<EmbeddedResource Include="Bundled/libs/*.d.ts" />` glob，无需生成代码 |
| libs_generated.go | Libs.g.cs | lib 文件名清单表，由拷贝脚本生成（04-codegen.md 第 3 族） |
| generate.go（`//go:build ignore`） | tools/generator/copy-libs.ts | 从 `_submodules/TypeScript` 同步 libs 的脚本 |
| noembed.go（`//go:build noembed`） | —（不移植） | Go 的 `go run` 开发模式变体；主计划 §一.4 只定了 EmbeddedResource 路线 |
| libs/*.d.ts ×108 | Bundled/libs/ 原样拷贝 | 与子模块钉扎 commit 同步 |

- **代码生成**：第 3 族（04-codegen.md）。
- **测试资产**：bundled_test.go（48 行）→ tests/Ts.Compiler.Tests。
- **危险点**：`bundled:///` 虚拟路径在 tspath 规范化中的特判必须原样保留；EmbeddedResource 读取在 AOT 下合法（无反射），但资源名大小写与 glob 顺序要固定（07-aot-constraints.md 内嵌资源政策）。
- **依赖**：repo（仅 generate.go/noembed.go）、tspath、vfs。生产嵌入路径仅依赖 tspath/vfs。

### 2.2 internal/diagnostics → Ts.Compiler / Diagnostics/ / Ts.Diagnostics

- **LOC** 9,423（其中 diagnostics_generated.go 8,626 为生成物）；测试 145。**阶段 0**（表生成）；运行时小文件随阶段 1 一起可用。
- **文件映射**：

| Go | C# | 说明 |
|---|---|---|
| diagnostics.go | Diagnostics.cs | `Message` 结构、Category、格式化占位符展开 |
| diagnostics_generated.go | Messages.g.cs | 2,154 条消息常量；generate-diagnostics.ts 从子模块 diagnosticMessages.json 生成，**门禁：计数=2,154**（主计划阶段 0） |
| extraDiagnosticMessages.json | tools/generator/ 同步副本 | tsgo 自有的追加消息，生成器输入之一 |
| stringer_generated.go | Category ToDisplayString()（stringer 族） | |
| loc_generated.go + loc/*.json.gz ×13 | Loc.g.cs + EmbeddedResource | 13 个本地化语言包；运行时接线归阶段 9（locale） |
| generate.go（`//go:build ignore`） | tools/generator/generate-diagnostics.ts | |

- **测试资产**：diagnostics_test.go → tests/Ts.Compiler.Tests。
- **危险点**：消息文本是 errors.txt 基线的字面组成部分——生成器不得做任何转义/空白规整；gzip 语言包解压用 `System.IO.Compression.GZipStream`（BCL，AOT 安全）。
- **依赖**：collections、core、json（→STJ）、locale、repo（仅生成器）。

### 2.3 internal/core → Ts.Compiler / Core/ / Ts.Core

- **LOC** 2,669；测试 176。**阶段 1**。
- **职责**：全仓库地基——切片/迭代工具、TextRange、WorkGroup 并发原语、LinkStore、编译选项 struct。
- **文件映射**（30 个文件，全列关键项）：

| Go | C# | 说明 |
|---|---|---|
| core.go | Core.cs | Map/Filter/Same* 切片助手（05-conventions.md 切片规则） |
| workgroup.go | WorkGroup.cs + LargeStackExecutor.cs | 并行/串行两实现；**所有 parse/bind/check 入口跑 64MB 大栈线程**（主计划 §一.5 0 号风险）；LargeStackExecutor 是阶段 0 交付物，落在此处 |
| semaphore.go | Semaphore.cs | → SemaphoreSlim 包装 |
| stack.go | Stack.cs | |
| context.go | Ts.Core 取消上下文 | Go context → CancellationToken 传递惯例（05-conventions.md） |
| linkstore.go | LinkStore.cs | → ReferenceEqualityComparer 的 Dictionary 包装（主计划 §一.6；签名细节见 03-checker-design.md） |
| arena.go | —（不移植） | 主计划 §一.1：删除全部 Arena，`arena.New()`→`new T()` |
| text.go / textchange.go | TextRange.cs / TextChange.cs | `struct {int Pos; int End;}`，UTF-16 偏移 |
| binarysearch.go / bfs.go / pattern.go | 同名 .cs | |
| compileroptions.go / buildoptions.go / watchoptions.go / typeacquisition.go / parsedoptions.go / projectreference.go | 同名 .cs | **compileroptions.go:176-184 用 reflect 做选项拷贝/遍历** → 并入 tsoptions 的 per-field switch 生成（§4.2） |
| version.go | Version.cs | `core.Version()`＝"7.1.0-dev"，**.tsbuildinfo 加载时精确字符串相等校验**（主计划 §一.5），一字不差 |
| nodemodules.go | NodeModules.cs | |
| tristate.go / scriptkind.go / scripttarget*.go / languagevariant.go / modulekind*.go + 5 个 *_stringer_generated.go | 枚举 + ToDisplayString() | stringer 族（04-codegen.md 第 6 族）：仅基线承重的枚举生成 |

- **测试资产**：bfs_test.go、pattern_test.go → tests/Ts.Compiler.Tests。
- **危险点**：reflect（compileroptions.go:176）；WorkGroup 的确定性开关是主计划 §四.3 验证义务的载体；LinkStore 引用相等语义不得误用值相等。
- **依赖**：collections、debug、json（→STJ）、stringutil、tspath。

### 2.4 internal/collections → Ts.Compiler / Collections/ / Ts.Collections

- **LOC** 840；测试 272。**阶段 1**。
- **文件映射**：cow.go→CowMap.cs（CopyOnWriteMap，EnterScope→IDisposable/using，主计划 §一.6）；multimap.go→MultiMap.cs；ordered_map.go→OrderedMap.cs；ordered_set.go→OrderedSet.cs；set.go→Set.cs；syncmap.go→SyncMap.cs（→ConcurrentDictionary 包装）；syncset.go→SyncSet.cs。
- **测试资产**：ordered_map_test.go、ordered_set_test.go、syncmap_test.go。
- **危险点**：ordered_map.go:224-247 在 JSON 序列化时用 reflect 解析键名（`resolveKeyName`）→ C# 写手写的 STJ `JsonConverter`（源生成上下文注册，07-aot-constraints.md）；**OrderedMap 是 map 迭代序审计规则的白名单容器**——需要确定序的调用点应已在 Go 侧用它，C# Dictionary 的意外插入序不得被当作等价物（05-conventions.md 审计规则）。
- **依赖**：json（→STJ）。

### 2.5 internal/tspath → Ts.Compiler / TsPath/ / Ts.TsPath

- **LOC** 1,435；测试 1,189（吨位大，全移植——路径语义是 Windows 风险的主防线）。**阶段 1**。
- **文件映射**：path.go→Path.cs（规范化/比较/组合核心）；extension.go→Extension.cs；ignoredpaths.go→IgnoredPaths.cs。
- **测试资产**：path_test.go、ignoredpaths_test.go、startsWithDirectory_test.go、untitled_test.go。
- **危险点**：path.go:676 `unsafe.String` 零拷贝——C# string/Span 原生覆盖，无对应物；路径比较必须 ordinal（`StringComparison.Ordinal`/OrdinalIgnoreCase，InvariantGlobalization 审计项，07-aot-constraints.md）；`bundled:///`、`^/untitled/` 等伪 scheme 特判保留。
- **依赖**：stringutil。

### 2.6 internal/vfs → Ts.Compiler / Vfs/ / Ts.Vfs.*

- **LOC** 3,131；测试 3,743。**阶段 1**。
- **子包映射**：

| Go 子包 | C# 命名空间/去向 | 说明 |
|---|---|---|
| vfs.go（根） | Ts.Vfs / Vfs.cs | FS 接口定义 |
| vfs/internal | Ts.Vfs.Internal | internal.go:165 `unsafe.String` → Span，无对应 |
| vfs/iovfs | Ts.Vfs.IoVfs | fs.FS 适配 → 以 C# 抽象重述（Go fs.FS 无 BCL 等价物，接口面 1:1 保留） |
| vfs/osvfs | Ts.Vfs.OsVfs | 真实文件系统；os.go 有 syscall/大小写探测，realpath 走 nativepath（§2.7） |
| vfs/cachedvfs | Ts.Vfs.CachedVfs | |
| vfs/trackingvfs | Ts.Vfs.TrackingVfs | |
| vfs/wrapvfs | Ts.Vfs.WrapVfs | |
| vfs/vfsmatch | Ts.Vfs.VfsMatch | 文件匹配算法（vfsmatch.go + MATCHING_ALGORITHM.md 文档一并拷入注释）；stringer_generated.go → stringer 族 |
| vfs/vfstest | tests/Ts.TestUtil（Ts.Vfs.VfsTest） | 测试内存 FS（Go 基于 fstest.MapFS + unsafe）→ 手写内存 FS |
| vfs/vfsmock | tests/Ts.TestUtil（Ts.Vfs.VfsMock） | mock_generated.go（Go moq 生成）→ **不移植生成器**，手写 mock 或接口默认实现 |

- **测试资产**：vfs_test.go、cachedvfs_test.go、iofs_test.go、osvfs 的 helpers_test/os_test/realpath_test、vfsmatch_test+bench_test、vfsmock/wrapper_test、vfstest_test。
- **危险点**：OS 特定（osvfs 的大小写敏感性探测、realpath）；Windows UNC/盘符路径（12-risks.md）；vfstest 的确定性枚举序（基线依赖目录枚举序时必须显式排序——map 迭代序审计）。
- **依赖**：collections、core、nativepath、tspath。

### 2.7 internal/nativepath → Ts.Compiler / NativePath/ / Ts.NativePath

- **LOC** 298；测试 185（symlink_windows_test.go）。**阶段 1**。
- **职责**：平台原生 realpath/symlink 原语（darwin `F_GETPATH`、linux readlink、windows `GetFinalPathNameByHandle`）。
- **文件映射**：realpath_darwin.go / realpath_linux.go / realpath_windows.go / realpath_other.go / symlink_windows.go / symlink_other.go / eintr_unix.go → **合并为 NativePath.cs 单文件 + OperatingSystem.IsWindows() 运行时分支**。Go 的编译期构建标签在 C# 中用运行时分支表达（三平台单一二进制内没有构建标签机制；05-conventions.md）。实现主体换 BCL：`File.ResolveLinkTarget(path, returnFinalTarget: true)`、`Directory.ResolveLinkTarget`、`Path.GetFullPath`；EINTR 重试（eintr_unix.go）由 BCL 内部处理，无对应。
- **危险点**：unsafe + syscall 全包——这是**接口保形、实现换体**的包（非逐行直译），保留导出函数名与语义，用 osvfs 的 realpath 测试 + vfs 测试群回归验收；Windows 尾随斜杠/短路径行为差异记入 12-risks.md。
- **依赖**：无。

### 2.8 internal/jsnum → Ts.Compiler / JsNum/ / Ts.JsNum

- **LOC** 584；测试 1,347（测试比生产大 2.3×，**全部移植**——这是阶段 1 的独立门禁）。**阶段 1**。
- **文件映射**：jsnum.go→JsNum.cs（Number 语义：ToInt32/ToUint32/取模/解析）；string.go→NumberToString.cs（ES6 数字→字符串版式层，主计划阶段 1 门禁详述）；pseudobigint.go→PseudoBigInt.cs。
- **测试资产**：jsnum_test.go、string_test.go（含 `{1e21,"1e+21"}` 等钉死表）、pseudobigint_test.go、ryu_test.go（Go 侧对 ryu 算法的往返测试→对 .NET 最短往返 ToString 的等价校验）。
- **危险点**：**门禁级字节保真**——数字格式承重于 .types 基线与 const-enum 内联（主计划 §五）；100 万随机 double 夹具逐位相等；-0→"0"、指数切换阈值、e+/e- 不补零。
- **依赖**：json（仅测试）、stringutil。

### 2.9 internal/stringutil → Ts.Compiler / StringUtil/ / Ts.StringUtil

- **LOC** 5,573（其中 js_case_generated.go 3,496 + identifier_parts_generated.go 1,391 为 Unicode 表生成物）；测试 108。**阶段 1**。
- **文件映射**：compare.go→Compare.cs（ordinal 比较器）；identifier.go→Identifier.cs；js_case.go→JsCase.cs；util.go→Util.cs；两个 `*_generated.go` Unicode 表 → `static ReadOnlySpan<T>` RVA 静态数据（04-codegen.md 第 5 族，一次性转写）；_scripts/generate-unicode-data.mts → tools/generator/（表再生成时用）。
- **测试资产**：js_case_test.go、util_test.go。
- **危险点**：大小写转换必须走自带表而非 `char.ToUpper`（文化无关性；InvariantGlobalization 下仍需与 Go 表逐码点一致）；表数据量大，验证生成的 Span 与 Go 数组逐元素相等。
- **依赖**：无。

### 2.10 internal/glob → Ts.Compiler / Glob/ / Ts.Glob

- **LOC** 349；无测试。**阶段 1**。glob.go→Glob.cs。危险点：无反射/unsafe；被 tsoptions（include/exclude）使用，行为由 tsoptions 基线测试间接覆盖。依赖：无。

### 2.11 internal/semver → Ts.Compiler / Semver/ / Ts.Semver

- **LOC** 712；测试 1,106（全移植）。**阶段 1**。version.go→Version.cs、version_range.go→VersionRange.cs。服务 module 的 typesVersions 与 packagejson。危险点：解析容错路径必须逐分支保真（错误 semver 不 panic）。依赖：无。

---

## 3. 阶段 2 · Scanner

### 3.1 internal/scanner → Ts.Compiler / Scanner/ / Ts.Scanner

- **LOC** 4,256；测试 21（scanner_test.go 极小——真验证是全语料 token 流 diff 门禁）。**阶段 2**。
- **文件映射**：scanner.go（2,918 行）→Scanner.cs；regexp.go（1,076 行）→RegExpScanner.cs（正则字面量扫描/标志校验）；unicodeproperties.go→UnicodeProperties.cs（属性表→ReadOnlySpan，第 5 族）；utilities.go→Utilities.cs（**含 GetECMALineAndUTF16CharacterOfPosition 等位置换算——UTF-16 决策的核心承重点**，主计划 §一.6）。
- **代码生成**：Unicode 表转写。
- **测试资产**：scanner_test.go；主验收 = dumptool token 流夹具 diff 100%（kind,pos,end,text；含 rescan/JSX/正则，08-testing-strategy.md）。
- **危险点**：**UTF-16 偏移改写**——Go 源码内部按字节偏移 + rune 解码，C# 直接按 UTF-16 code unit 前进；这是全移植唯一的系统性语义改写点，逐函数直译时所有 `pos += utf8.RuneLen(ch)` 类代码要换成 code unit 步进（05-conventions.md UTF-16 节）；代理对/星面字符路径必须被夹具覆盖。
- **依赖**：ast、collections、core、debug、diagnostics、jsnum、stringutil。**注意 scanner↔ast 相互引用问题在 Go 侧不存在（scanner 单向依赖 ast）**，阶段 2 先行时需要 ast 包中 Kind 枚举与 diagnostic 类型先到位——实际排程上 `SyntaxKind.g.cs`、`Diagnostics/Messages.g.cs` 均为阶段 0 生成物，ast/diagnostic.go 的小运行时类型随阶段 2 预移（09-phases.md 重叠 DAG）。

---

## 4. 阶段 3/4 · AST、Parser、选项与程序骨架

### 4.1 internal/ast → Ts.Compiler / Ast/ / Ts.Ast

- **LOC** 20,847（其中 ast_generated.go 10,047 为生成物，重新生成为 C#）；测试 824。**阶段 3**（生成器自举在阶段 0）。
- **文件映射**：

| Go | C# | 说明 |
|---|---|---|
| ast.go（3,059 行，Node 核心/NodeList/工厂钩子） | Node.cs + NodeList.cs（手写）+ 生成物 | 设计全文见 02-ast-design.md；header+data 拆分坍缩为类继承（主计划 §一.1，ast.go:179-186） |
| ast_generated.go（10,047 行） | Nodes.g.cs / NodeCasts.g.cs / NodeFactory.g.cs / Visitors.g.cs / Accessors.g.cs（与 SyntaxKind.g.cs、GlobalUsings.g.cs 合计 7 个物理文件，R2 定稿，02-ast-design.md §16） | generate-csharp-ast.ts（04-codegen.md 第 1 族），**生成代码入库** |
| kind_generated.go / kind_stringer_generated.go | SyntaxKind.g.cs（short 枚举 + ToDisplayString） | |
| utilities.go（4,565 行） | Utilities.cs | 手写直译；nil 接收者方法 → 扩展方法（05-conventions.md） |
| visitor.go | Visitor.cs | 深递归点 |
| deepclone.go | DeepClone.cs | |
| diagnostic.go | Diagnostic.cs | ast 级诊断类型（scanner/parser 共用，可随阶段 2 预移） |
| symbol.go / symbolflags.go / checkflags.go / flow.go / nodeflags.go / modifierflags.go / tokenflags.go / functionflags.go / subtreefacts.go / precedence.go / parseoptions.go / ids.go | 同名 .cs | ids.go 的原子 ID 分配 → Interlocked |
| **positionmap.go** | **不移植** | 主计划 §一.6：UTF-16 决策删除 PositionMap 与 rune 机制 |

- **测试资产**：deepclone_test.go 移植；**positionmap_test.go 不移植**（随 PositionMap 删除）。
- **危险点**：生成器三规则（最派生基/mixin 按字段名去重/brand→接口，主计划 §五 对抗验证修正）；visitor/deepclone 深递归→大栈线程；`Expression` 等 Go 类型别名 → `global using Expression = Ts.Ast.Node;`。
- **依赖**：collections、core、debug、diagnostics、locale、stringutil、tspath。

### 4.2 internal/parser → Ts.Compiler / Parser/ / Ts.Parser

- **LOC** 9,071；测试 233 + fuzz 语料。**阶段 3**。
- **文件映射**：parser.go（6,827 行，<15k 不强拆，允许按需 partial）→Parser.cs；jsdoc.go（1,355 行）→JSDoc.cs（JSDoc 专名原样保留，R6）；reparser.go（748 行，JSDoc 类型重解析）→Reparser.cs；references.go→References.cs；types.go→Types.cs；utilities.go→Utilities.cs。
- **测试资产**：parser_test.go；testdata/fuzz/FuzzParser 的 10 个语料文件 → 拷入 tests 作固定回归输入（.NET 无 go fuzz 等价物，语料当作普通用例）。
- **危险点**：**深递归 0 号风险主现场**（表达式/类型嵌套），入口必须经 LargeStackExecutor；lookahead/快照恢复的状态机逐字段保真；解析诊断 (code,pos,len) 三元组是阶段 3 门禁货币。
- **依赖**：ast、collections、core、debug、diagnostics、scanner、stringutil、tspath。

### 4.3 internal/astnav → Ts.Compiler / AstNav/ / Ts.AstNav

- **LOC** 783；测试 640。**阶段 3**。tokens.go→Tokens.cs（GetTokenAtPosition/GetTouchingToken 族，ls 与 checker 共用）。测试 tokens_test.go 依赖官方子模块语料（repo 包定位），归 tests/Ts.Compiler.Tests。依赖：ast、core、scanner。

### 4.4 internal/testutil → tests/Ts.TestUtil / Ts.TestUtil.*

- **LOC** 5,730（本身即测试基架，不进 src/）；测试 25。**阶段 3 起滚动移植**（各子包随其服务的门禁阶段到位）。
- **子包映射与到位阶段**：

| Go 子包 | C# | 到位阶段 | 说明 |
|---|---|---|---|
| testutil.go / stringtestutil / filefixture / fixtures | Ts.TestUtil 根 | 3 | |
| baseline/ | Baseline.cs | 3 | diff+accept 工作流（08-testing-strategy.md） |
| parsetestutil/ | ParseTestUtil.cs | 3 | |
| harnessutil/（harnessutil.go、recorderfs.go、sourcemap_recorder.go） | HarnessUtil/ | 5–6 | **sourcemap_recorder.go:351-353：续行标记终列 = JS 行 UTF-8 字节长−1 → Encoding.UTF8.GetByteCount 垫片**（主计划 §一.6 垫片 2） |
| tsbaseline/（error_baseline、js_emit_baseline、module_resolution_baseline、sourcemap_baseline、sourcemap_record_baseline、type_symbol_baseline、util） | TsBaseline/ | 5–6 | **error_baseline 波浪线按 Unicode 码点计数垫片**（主计划垫片 1）；type_symbol_baseline.go:236 字节 pad 路径休眠，记 12-risks.md 不移植行为 |
| emittestutil/ | EmitTestUtil.cs | 6 | |
| jstest/（node.go：调 Node.js 执行产出 JS） | JsTest.cs | 6 | 需要环境 node，可 CI 条件化 |
| fsbaselineutil/ | FsBaselineUtil.cs | 7 | |
| lsptestutil/ | LspTestUtil.cs | 8 | |
| projecttestutil/（clientmock_generated.go、npmexecutormock_generated.go） | ProjectTestUtil/ | 8 | moq 生成 mock → 手写 |
| autoimporttestutil/ | AutoImportTestUtil.cs | 8 | |
| race/ | —（不移植） | — | Go race detector 构建标签，.NET 无对应；调用点删除或空化 |

- **依赖**：几乎全仓库（测试基架天性）；它是唯一允许晚绑定依赖的"包"。

### 4.5 internal/testrunner → tests/Ts.Conformance.Tests / Ts.TestRunner

- **LOC** 889；测试 106。**阶段 3**（主计划明示随 parser 移植）。
- **文件映射**：runner.go→Runner.cs；compiler_runner.go→CompilerRunner.cs（conformance 跑批器：读 `_submodules/TypeScript/tests/cases/{compiler,conformance}`，比对 `testdata/baselines/reference/` 的 49,323 个基线）；test_case_parser.go→TestCaseParser.cs（`// @option:` 指令与多文件 test 解析）。
- **测试资产**：compiler_runner_test.go、test_case_parser_test.go。
- **危险点**：@option 解析容错、文件切分换行语义（\r\n）逐字保真——它是阶段 5/6 门禁货币的制造机，本身错了会掩盖 checker 错误；跑批并行度与基线 accept 流程设计见 08-testing-strategy.md。
- **依赖**：ast、checker、core、parser、repo、scanner、testutil、tsoptions、tspath、vfs。

### 4.6 internal/binder → Ts.Compiler / Binder/ / Ts.Binder

- **LOC** 3,555；测试 46。**阶段 4**。
- **文件映射**：binder.go→Binder.cs（符号表构建 + 控制流图）；nameresolver.go→NameResolver.cs（checker 共用的名字解析）；referenceresolver.go→ReferenceResolver.cs。
- **测试资产**：binder_test.go（46 行，小）；主验收 = symbol/flow 转储夹具 100%（阶段 4 门禁）。
- **危险点**：深递归（bind 逐节点递归）；FlowNode 图的构建顺序决定 flow id → 转储 diff 敏感；SymbolTable 迭代处按 map 序审计规则逐点标注。
- **依赖**：ast、collections、core、debug、diagnostics、scanner、tspath。

### 4.7 internal/tsoptions → Ts.Compiler / TsOptions/ / Ts.TsOptions

- **LOC** 6,144；测试 2,573。**阶段 4**。
- **文件映射**（关键项）：

| Go | C# | 说明 |
|---|---|---|
| commandlineoption.go | CommandLineOption.cs | 选项元数据表 |
| commandlineparser.go | CommandLineParser.cs | |
| tsconfigparsing.go（1,870 行） | TsConfigParsing.cs | :333-357 reflect 判型 → C# 模式匹配（`is string`/`is bool`/OrderedMap 判型），无反射 |
| parsinghelpers.go | ParsingHelpers.cs | :608-609 reflect 选项拷贝 → **生成 per-field switch** |
| declscompiler.go（1,264 行）/ declsbuild.go / declswatch.go / declstypeacquisition.go | Decls*.cs | declscompiler.go:1203 反射遍历 → 生成的 ~150 字段 switch（主计划 §一.5；04-codegen.md 扩展族） |
| showconfig.go | ShowConfig.cs | :169 反射序列化 → 同上生成 switch |
| enummaps.go / namemap.go / errors.go / diagnostics.go | 同名 .cs | |
| parsedcommandline.go / parsedbuildcommandline.go / wildcarddirectories.go | 同名 .cs | |
| tsoptionstest/ | tests/Ts.TestUtil（Ts.TsOptions.Test） | vfs 化的 config 解析测试 host |

- **代码生成**：选项 per-field switch 生成器（覆盖 core/compileroptions.go:176、execute/incremental/snapshottobuildinfo.go:288、execute/tsc/init.go:87 等全部选项反射点——**单一生成器统一供给**，避免四处手写漂移）。
- **测试资产**：commandlineparser_test.go（572 行）、tsconfigparsing_test.go（1,568 行）、parsedcommandline_test.go、parsinghelpers_test.go、decls_test.go、export_test.go、wildcarddirectories_test.go；tsoptions 基线测试是阶段 4 门禁项。
- **危险点**：**全仓库反射最密集的包**（07-aot-constraints.md 的第一号客户）；tsconfig JSON 解析的容错/注释/尾逗号语义；extends 链合并顺序。
- **依赖**：ast、collections、core、debug、diagnostics、glob、jsnum、locale、module、outputpaths、parser、scanner、stringutil、tspath、vfs。

### 4.8 internal/module → Ts.Compiler / Module/ / Ts.Module

- **LOC** 2,780；测试 416。**阶段 4**（主计划"模块解析器 2.8k"）。
- **文件映射**：resolver.go→Resolver.cs（Node16/NodeNext/Bundler 全解析算法）；cache.go→Cache.cs（并发解析缓存）；types.go→Types.cs；util.go→Util.cs。
- **测试资产**：resolver_test.go；门禁 = 模块解析 trace 夹具匹配（阶段 4）。
- **危险点**：解析 trace 文本是夹具货币——日志字符串逐字保真；缓存键含大小写规范化路径（ordinal 审计）；package.json exports/imports 条件解析顺序承重。
- **依赖**：ast、collections、core、diagnostics、packagejson、semver、stringutil、tspath、vfs。

### 4.9 internal/packagejson → Ts.Compiler / PackageJson/ / Ts.PackageJson

- **LOC** 662；测试 292。**阶段 4**。
- **文件映射**：packagejson.go→PackageJson.cs；exportsorimports.go→ExportsOrImports.cs；jsonvalue.go→JsonValue.cs（宽松 JSON 值模型）；expected.go→Expected.cs；validated.go→Validated.cs；cache.go→Cache.cs。
- **危险点**：expected.go:52-59 `reflect.TypeFor[T]().Kind()` 泛型判型 → C# `typeof(T) == typeof(string)` 模式（typeof 在禁反射政策的允许清单内，07-aot-constraints.md）；JSON 解析换 STJ 后无效值容错路径逐分支比对。
- **测试资产**：packagejson_test.go、exportsorimports_test.go、jsonvalue_test.go、expected_test.go。
- **依赖**：collections、core、diagnostics、json（→STJ）、semver、tspath。

### 4.10 internal/symlinks → Ts.Compiler / Symlinks/ / Ts.Symlinks

- **LOC** 134；测试 364（含 bench）。**阶段 4**。knownsymlinks.go→KnownSymlinks.cs（已知符号链接缓存，modulespecifiers/compiler 用）。测试 knownsymlinks_test.go 移植、bench 转 BenchmarkDotNet（不设门禁）。依赖：ast、collections、core、module、tspath。

### 4.11 internal/outputpaths → Ts.Compiler / OutputPaths/ / Ts.OutputPaths

- **LOC** 307；无测试。**阶段 4**。outputpaths.go→OutputPaths.cs、commonsourcedirectory.go→CommonSourceDirectory.cs。outDir/declarationDir/rootDir 推导——行为被 emit 基线与 tsoptions 测试覆盖。依赖：ast、core、tspath。

### 4.12 internal/diagnosticwriter → Ts.Compiler / DiagnosticWriter/ / Ts.DiagnosticWriter

- **LOC** 506；无测试。**阶段 4**（体量小、依赖已备；服务阶段 5 errors.txt 基线——testutil/tsbaseline/js_emit_baseline.go:11 直接引用——与阶段 7 CLI `--pretty` 输出）。
- **文件映射**：diagnosticwriter.go→DiagnosticWriter.cs（诊断格式化：带颜色/上下文行/相关信息缩进）。
- **危险点**：输出逐字符进 errors.txt 基线与 CLI 快照测试；行列号为 UTF-16（主计划 §一.6 判别性证据）；ANSI 颜色码与截断规则原样。
- **依赖**：ast、core、diagnostics、locale、scanner、tspath。

### 4.13 internal/compiler → Ts.Compiler / Compiler/ / Ts.Compiler

- **LOC** 5,658；测试 558。**阶段 4（fileloader/Program 子集）+ 阶段 5（checkerpool）+ 阶段 6（emitter）**。
- **文件映射**：

| Go | C# | 阶段 | 说明 |
|---|---|---|---|
| program.go（2,195 行） | Program.cs | 4 | Program 骨架/诊断聚合；GetTypeChecker 面到 5 才通 |
| fileloader.go（791 行） | FileLoader.cs | 4 | 并行文件加载（WorkGroup + channel→Channels） |
| filesparser.go（567 行） | FilesParser.cs | 4 | |
| host.go / emitHost.go | Host.cs / EmitHost.cs | 4/6 | |
| fileInclude.go / includeprocessor.go / processingDiagnostic.go | 同名 .cs | 4 | 文件包含理由追踪（--explainFiles） |
| projectreferencefilemapper.go / projectreferenceparser.go / projectreferencedtsfakinghost.go | 同名 .cs | 4 | 项目引用 |
| checkerpool.go（167 行） | CheckerPool.cs | 5 | N 个 checker、文件 index%N 钉扎（主计划 §一.2 并发模型） |
| emitter.go（531 行） | Emitter.cs | 6 | |

- **测试资产**：program_test.go（阶段 4）、emit_test.go（阶段 6）。
- **危险点**：fileloader 的并行任务图必须保序（文件列表顺序决定 file index → 决定 checker 钉扎 → 决定诊断归属，确定性检验主计划 §四.3）；tracing 生产 import → 需要阶段 4 先落 Ts.Tracing 接口骨架（§9.2）。
- **依赖**：ast、binder、checker、collections、core、diagnostics、json（→STJ）、locale、module、modulespecifiers、outputpaths、packagejson、parser、printer、scanner、sourcemap、stringutil、symlinks、tracing、transformers、tsoptions、tspath、vfs（checker/printer/transformers 的引用在阶段 4 以接口/空桩形式存在，09-phases.md 重叠 DAG）。

---

## 5. 阶段 5 · Checker 群

### 5.1 internal/checker → Ts.Compiler / Checker/ / Ts.Checker

- **LOC** 59,822；测试 153（checker_test.go、tracer_test.go——本包几乎不靠单测，靠 conformance 门禁）。**阶段 5**。
- **文件映射**（26 个文件全列）：

| Go | C# | 说明 |
|---|---|---|
| checker.go（31,926 行） | `Checker.*.cs` ×22 partial（03-checker-design.md §4.2 权威拆分表，见下） | 唯一 >15k 手写巨文件 |
| types.go（1,459 行） | Types.cs | 24 type struct → 单继承（主计划 §一.2；InterfaceType:TypeReference 次序 types.go:994）；**手工移植，不走生成** |
| relater.go（5,006 行） | Checker.Relater.cs | 关系检查（assignability/comparability）；关系缓存 per-checker |
| nodebuilderimpl.go（3,585 行） | NodeBuilderImpl.cs | |
| nodebuilder.go / nodebuilderscopes.go / nodebuilder_hover.go（597） | NodeBuilder.cs / NodeBuilder.Scopes.cs / NodeBuilder.Hover.cs | |
| pseudotypenodebuilder.go（765） | PseudoTypeNodeBuilder.cs | |
| flow.go（2,734 行） | Checker.Flow.cs | 控制流窄化 |
| inference.go（1,651 行） | Checker.Inference.cs | |
| grammarchecks.go（2,202 行） | Checker.GrammarChecks.cs | |
| jsx.go（1,482 行） | Checker.Jsx.cs | |
| jsdoc.go | Checker.JSDoc.cs | JSDoc 专名原样保留（R6） |
| emitresolver.go（1,322 行） | EmitResolver.cs | printer 侧的解析器接口实现 |
| services.go（1,140 行） | Checker.Services.cs | ls 用的 checker 服务面 |
| utilities.go（1,844 行） | Utilities.cs | |
| mapper.go | Mapper.cs | TypeMapper |
| nodecopy.go（900） | NodeCopy.cs | |
| symbolaccessibility.go（876） | Checker.SymbolAccessibility.cs | |
| symboltracker.go | SymbolTracker.cs | |
| exports.go | Checker.Exports.cs | |
| printer.go | Checker.Printer.cs | checker 侧类型打印 |
| tracer.go | Tracer.cs | --generateTrace 挂接（nil *Tracer 为合法 no-op，tracer.go:10-13→C# null 检查同形） |
| stringer_generated.go | Stringers.g.cs（stringer 族 ToDisplayString） | |

- **checker.go → partial class 拆分表**（主计划 §一.3 要求记入本清单。**核验事实：checker.go 无任何分节 banner 注释、共 1,484 个顶层函数**——主计划"按 Go 原有分节注释拆"在此文件上无锚点可用。**拆分表唯一权威 = 03-checker-design.md §4.2 的 22 文件表**（R1 裁定：按 grep 核验过的函数簇锚点拆连续行范围，函数为原子、不得跨文件劈开一个函数；行域、锚点函数与两处例外登记以 03 为准），本处不再自有分表，仅镜像文件名清单：

  Checker.cs、Checker.Keys.cs、Checker.Construction.cs、Checker.NameLookup.cs、Checker.Statements.cs、Checker.Unused.cs、Checker.Expressions.cs、Checker.OperatorExpressions.cs、Checker.Diagnostics.cs、Checker.SymbolResolution.cs、Checker.SymbolTypes.cs、Checker.Members.cs、Checker.Signatures.cs、Checker.Instantiation.cs、Checker.TypeNodes.cs、Checker.TypeCreation.cs、Checker.UnionIntersection.cs、Checker.IndexedAccess.cs、Checker.Constraints.cs、Checker.AliasReferences.cs、Checker.ContextualTypes.cs、Checker.Awaited.cs（×22）。

- **代码生成**：仅 stringer；Type 层次与缓存签名全手写（03-checker-design.md）。
- **移植顺序**（主计划阶段 5）：types → 类型构造/驻留 → relations → 实例化/推断 → 窄化/flow → check* 长尾；每步以 errors.txt 分级门禁护航（25%→60%→90%→99.5%→100%）。
- **危险点**：深递归全域（0 号风险）；**map 迭代序审计密度全仓最高**（联合类型驻留、符号表遍历——每个 `for range` 逐点标注，05-conventions.md）；结构键缓存 → `readonly record struct`（CacheHashKey 等，checker.go:660 `map[CacheHashKey]*Type`）；Type.checker 反向指针 = 并发安全根基（类型永不跨 checker 共享）；4-checker 并行输出与单 checker 全等的终检义务。
- **依赖**：ast、astnav、binder、collections、core、debug、diagnostics、evaluator、jsnum、module、modulespecifiers、nodebuilder、parser、printer、pseudochecker、scanner、stringutil、tracing、tsoptions、tspath。（不依赖 compiler——checker_test.go 才用，生产无环。）

### 5.2 internal/evaluator → Ts.Compiler / Evaluator/ / Ts.Evaluator

- **LOC** 168；无测试。**阶段 5**（printer 也依赖它，先于/随 checker 落地皆可）。evaluator.go→Evaluator.cs：常量表达式求值（enum/模板字面量），jsnum 语义承重（const enum 内联进 .js 基线）。依赖：ast、core、jsnum。

### 5.3 internal/nodebuilder → Ts.Compiler / NodeBuilder/ / Ts.NodeBuilder（微包）

- **LOC** 78（未列入任务 LOC 清单，实测；见 §13.1）；无测试。**阶段 5 前置**（printer 生产依赖它——它存在的目的就是让 printer 不依赖 checker，types.go:1-5 注释）。
- types.go→Types.cs：SymbolTracker 接口（13 个方法）+ `nodebuilder.Flags` 枚举（与 TypeFormatFlags 同步义务的注释保留）。危险点：无。依赖：ast。

### 5.4 internal/pseudochecker → Ts.Compiler / PseudoChecker/ / Ts.PseudoChecker

- **LOC** 1,126；无测试。**阶段 5**（主计划将其计入 checker 群 ≈61k）。
- checker.go→PseudoChecker.cs、lookup.go→Lookup.cs、type.go→Type.cs：无完整 checker 的声明发射快路径（isolatedDeclarations 风格的语法制导类型构建）。危险点：与真 checker 输出的一致性由 .d.ts 基线间接验收（阶段 6）。依赖：ast、core、debug。

### 5.5 internal/modulespecifiers → Ts.Compiler / ModuleSpecifiers/ / Ts.ModuleSpecifiers

- **LOC** 2,280；测试 342。**阶段 5**（checker 生产依赖：nodebuilder 为声明发射合成 import 说明符；阶段 8 的 ls auto-import 复用）。
- 文件映射：specifiers.go→Specifiers.cs（主算法）；compare.go→Compare.cs；preferences.go→Preferences.cs；types.go→Types.cs；util.go→Util.cs。
- **测试资产**：specifiers_test.go。
- **危险点**：候选说明符排序是确定性输出的一部分（.d.ts 基线含合成 import）；路径大小写与 symlink 展开（依赖 symlinks 包缓存）。
- **依赖**：ast、collections、core、debug、module、outputpaths、packagejson、stringutil、symlinks、tsoptions、tspath。

---

## 6. 阶段 6 · Emit 群

### 6.1 internal/printer → Ts.Compiler / Printer/ / Ts.Printer

- **LOC** 11,442；测试 3,385。**阶段 6**（主计划排程注：只依赖 AST，可与阶段 5 部分并行；前置微包 nodebuilder §5.3）。
- **文件映射**（15 文件全列）：printer.go（6,280 行，<15k 单文件保留）→Printer.cs；factory.go→Factory.cs（transform 期节点工厂扩展）；emitcontext.go→EmitContext.cs；emitresolver.go→EmitResolver.cs（接口，checker/emitresolver.go 实现）；emithost.go→EmitHost.cs；emittextwriter.go→EmitTextWriter.cs；textwriter.go→TextWriter.cs；changetrackerwriter.go→ChangeTrackerWriter.cs（ls 改写用）；singlelinestringwriter.go→SingleLineStringWriter.cs；namegenerator.go→NameGenerator.cs；sourcefilemetadataprovider.go→SourceFileMetadataProvider.cs；helpers.go→Helpers.cs；utilities.go→Utilities.cs；emitflags.go/generatedidentifierflags.go→枚举文件。
- **测试资产**：printer_test.go、namegenerator_test.go、utilities_test.go。
- **危险点**：输出即 .js/.d.ts 基线字节（阶段 6 门禁 100% 字节匹配）——注释保留/换行/缩进/字符串引号选择全部承重；深递归（打印嵌套表达式）；NameGenerator 的临时名分配序确定性。
- **依赖**：ast、binder、collections、core、debug、evaluator、nodebuilder、scanner、sourcemap、stringutil、tsoptions、tspath。

### 6.2 internal/transformers → Ts.Compiler / Transformers/ / Ts.Transformers.*

- **LOC** 24,323；测试 376。**阶段 6**。
- **子包映射**：

| Go | C# 命名空间 | 关键文件（行数） |
|---|---|---|
| 根（chain.go、transformer.go、destructuring.go、modifiervisitor.go、utilities.go） | Ts.Transformers | 变换链装配 |
| estransforms/（17 文件） | Ts.Transformers.EsTransforms | classfields.go(3,618)、esdecorator.go(2,751)、async.go(995)、forawait.go(856)、using.go(799)、objectrestspread、optionalchain、nullishcoalescing、logicalassignment、exponentiation、taggedtemplate、optionalcatch、namedevaluation、classthis、definitions、usestrict、utilities |
| declarations/ | Ts.Transformers.Declarations | transform.go(2,957)、diagnostics.go(735)、tracker.go、util.go —— .d.ts 发射 |
| moduletransforms/ | Ts.Transformers.ModuleTransforms | commonjsmodule.go(2,162)、esmodule.go、impliedmodule.go、externalmoduleinfo.go、utilities.go |
| tstransforms/ | Ts.Transformers.TsTransforms | legacydecorators.go(1,038)、runtimesyntax.go(995)、typeeraser.go、importelision.go、metadata.go、typeserializer.go、utilities.go |
| jsxtransforms/ | Ts.Transformers.JsxTransforms | jsx.go(1,209) |
| inliners/ | Ts.Transformers.Inliners | constenum.go（依赖 evaluator 的数字格式！） |

- **测试资产**：tstransforms/importelision_test.go、typeeraser_test.go；主验收 = .js 基线。
- **危险点**：深递归 visitor；**生产依赖 checker**（declarations/emit 诊断经 EmitResolver 之外亦直接引 checker 包）——装配依赖 DAG 时 transformers 在 checker 之后；变换链顺序（chain.go）逐项保真；EmitContext 的自动生成名与 helper 注入顺序决定输出字节。
- **依赖**：ast、binder、checker、collections、core、debug、diagnostics、jsnum、modulespecifiers、nodebuilder、outputpaths、printer、scanner、stringutil、tspath。

### 6.3 internal/sourcemap → Ts.Compiler / SourceMap/ / Ts.SourceMap

- **LOC** 1,013；测试 383。**阶段 6**。
- 文件映射：generator.go→Generator.cs（VLQ/mappings 生成）；decoder.go→Decoder.cs；source_mapper.go→SourceMapper.cs；lineinfo.go→LineInfo.cs；source.go→Source.cs；util.go→Util.cs。
- **测试资产**：generator_test.go。
- **危险点**：.js.map 是阶段 6 字节门禁的一部分；**列偏移 = UTF-16**（主计划 §一.6 已核验；UTF-8 字节长只出现在测试基架 sourcemap_recorder 的垫片里，见 §4.4）；JSON 写出走字节保真约定（01-architecture.md）。
- **依赖**：core、debug、json（→STJ）、scanner、stringutil、tspath。

---

## 7. 阶段 7 · tsc CLI

### 7.1 internal/execute → src/Ts.Cli / Execute/ / Ts.Execute.*（项目归属见 §13.2）

- **LOC** 9,348；测试 10,820（execute 是测试比生产大的重验证区）。**阶段 7**。
- **子包映射**：

| Go | C# 命名空间 | 关键文件 |
|---|---|---|
| 根：tsc.go(376)、watcher.go(443) | Ts.Execute | tsc.go 是 CLI 主循环（**import format 与 ls/lsutil**，tsc.go:16,19——execute 因此不能进 Ts.Compiler）；watcher.go:418 `reflect.DeepEqual` 比较 ParsedConfig → 手写结构相等比较器 |
| tsc/ | Ts.Execute.Tsc | compile.go、emit.go、diagnostics.go、statistics.go、help.go、init.go（:87 reflect 遍历默认值 → 选项 switch 生成器供给）、extendedconfigcache.go |
| build/ | Ts.Execute.Build | orchestrator.go（--build DAG 调度）、buildtask.go、uptodatestatus.go、host.go、compilerHost.go、parseCache.go |
| incremental/ | Ts.Execute.Incremental | snapshot.go（**ComputeHash:32——xxh3-128 → .tsbuildinfo version/signature，承重**，主计划 §一.6：哈希 UTF-8 字节、小写 hex）、buildInfo.go(630，19 字段声明序/omitzero/7 多态形状 buildInfo.go:34-448)、snapshottobuildinfo.go（:288 reflect 选项遍历 → 生成 switch）、buildinfotosnapshot.go、programtosnapshot.go、affectedfileshandler.go、emitfileshandler.go、incremental.go、program.go、referencemap.go、host.go |
| watchmanager/ | Ts.Execute.WatchManager | watchmanager.go、watchbackend.go（**生产依赖 fswatch**，§13.3） |
| tsctests/ | tests/Ts.Conformance.Tests（Ts.Execute.TscTests；R18：不设 Ts.Cli.Tests） | runner.go、sys.go、fs.go、mock_watch_backend.go、readablebuildinfo.go（readable-baseline 写出器：2 空格缩进 + size=紧凑文本字节长，主计划 §一.5） |

- **测试资产**：tsctests 下 tsc_test.go、tscbuild_test.go、tscwatch_test.go、showconfig_test.go、watcher_race_test.go、extendedconfigcache_test.go、build/graph_test.go——全部移植，是阶段 7 门禁本体；归 tests/Ts.Conformance.Tests（R18：tests/ 五个核心项目不变，不设 Ts.Cli.Tests；08 的 Ts.Conformance.AotHost 与 Ts.Benchmarks 为合法后期追加，各自阶段建成时加入 slnx）。
- **危险点**：.tsbuildinfo 字节保真全家桶（UnsafeRelaxedJsonEscaping/键序/omitzero/多态形状/控制字符短转义，主计划 §一.5）；xxh3 承重哈希；watch 去抖与事件合并时序（mock backend 使测试确定化）；reflect ×3 处；「增量=全量输出」门禁。
- **依赖**：ast、binder、checker、collections、compiler、core、diagnostics、diagnosticwriter、format、fswatch、json（→STJ）、locale、ls(lsutil)、outputpaths、packagejson、parser、pprof、testutil（仅测试助手引用）、tracing、tsoptions、tspath、vfs。

### 7.2 internal/pprof → src/Ts.Cli / Profiling/ / Ts.Profiling —— **非 1:1，归宿 EventPipe/诊断**

- **LOC** 169；无测试。**阶段 7**（CLI `--pprofDir`）/ 阶段 9（lsp/api 的按需剖析）。
- **Go 面**：pprof.go——`BeginProfiling`（进程级 CPU+allocs profile 写 `<pid>-cpuprofile.pb.gz`）、`CPUProfiler`（LSP 按需启停）、`SaveHeapProfile`/`SaveAllocProfile`。
- **C# 归宿（主计划 §一.4/§二 精神：诊断类不做字节复刻）**：**不移植 pprof 格式**。保留同名 API 面（BeginProfiling/CpuProfiler/SaveHeapProfile）供调用点 1:1 直译，实现改为 .NET 诊断栈：进程内 `System.Diagnostics.Tracing.EventSource` 计数 + 依托 EventPipe（`dotnet-trace`/`dotnet-gcdump` 附加，或 Microsoft.Diagnostics.NETCore.Client 自采样写 `.nettrace`）。输出文件扩展名与格式**允许偏离**（.nettrace 而非 .pb.gz），CLI flag 名保留。AOT 注意：EventPipe 在 Native AOT 受支持，运行时自反射采样不可用——以 EventSource 事件为主。设计自由度在 07-aot-constraints.md 与 12-risks.md 登记为「非保真区」。
- **依赖**：无 internal 依赖（被 execute、lsp、api 引用）。

---

## 8. 阶段 8 · 语言服务

### 8.1 internal/ls → src/Ts.LanguageService / Ls/ / Ts.Ls.*

- **LOC** 38,933（根 28,968 + autoimport 5,353 + lsutil 2,762 + change 1,423 + lsconv 427）；测试 2,877。**阶段 8**（主计划：阶段 5 达 ~90% 后启动）。
- **根包关键文件**（42 文件，>900 行者全列）：completions.go(6,295)、findallreferences.go(2,654)、string_completions.go(2,211)、signaturehelp.go(1,421)、codeactions_fixmissingtypeannotation.go(1,419)、utilities.go(1,404)、hover.go(1,133)、callhierarchy.go(1,103)、organizeimports.go(954)、inlay_hints.go(927)；其余（definition、rename、codelens、semantictokens、folding、symbols、documenthighlights、linkedediting、selectionranges、diagnostics、codeactions*、jsdoc*、importTracker、sourcedefinition、source_map、autoinsert、crossproject、file_rename、displaypartswriter、format.go、host.go、languageservice.go、api.go、constants.go）逐一同名映射到 Ls/*.cs。
- **子包**：autoimport/ → Ts.Ls.AutoImport（index.go、registry.go、aliasresolver.go、export.go+stringer、extract.go、fix.go、import_adder.go、specifiers.go、view.go、util.go）；change/ → Ts.Ls.Change（tracker.go、trackerimpl.go、delete.go——文本改写器）；lsconv/ → Ts.Ls.LsConv（converters.go、linemap.go——LSP 位置↔内部位置换算，**PositionMap 删除后 UTF-16↔行列换算的唯一活跃现场**）；lsutil/ → Ts.Ls.LsUtil（utilities.go、symbol_display.go、userpreferences.go、asi.go、children.go、completednode.go、formatcodeoptions.go、organizeimports.go——被 format 与 execute 依赖，**阶段 7 需先行到位 lsutil 子集**，§13.2）。
- **测试资产**：format_test.go（根）、autoimport 的 index_test/registry_test/util_test/aliasresolver_crash_test（+testmain）、lsconv/converters_test.go、lsutil/userpreferences_test.go、utilities_test.go → tests/Ts.LanguageService.Tests；主验收 = fourslash 分级门禁。
- **危险点**：lsutil/userpreferences.go:289 `map[reflect.Type]func` 类型解析表 → 手写 switch（禁反射）；completions 的排序/去重是 fourslash 基线敏感点（map 序审计密集区第二名）；crossproject.go 依赖 project 子包（§13.1）。
- **依赖**：ast、astnav、binder、bundled、checker、collections、compiler、core、debug、diagnostics、diagnosticwriter、evaluator、format、jsnum、json（→STJ）、locale、lsp/lsproto（仅协议类型）、module、modulespecifiers、nodebuilder、outputpaths、packagejson、parser、printer、project/dirty+project/logging（仅助手子包）、scanner、sourcemap、stringutil、symlinks、tsoptions、tspath、vfs。

### 8.2 internal/format → src/Ts.LanguageService / Format/ / Ts.Format

- **LOC** 4,291；测试 664。**阶段 8**（execute 于阶段 7 引用其入口——见 §13.2 排序注）。
- 文件映射：span.go(1,252)→Span.cs（格式化主扫描）；indent.go(821)→Indent.cs；rulecontext.go(629)→RuleContext.cs；rules.go(450)→Rules.cs；rulesmap.go→RulesMap.cs；rule.go→Rule.cs；context.go→Context.cs；scanner.go→FormattingScanner.cs；api.go→Api.cs；util.go→Util.cs。
- **测试资产**：format_test.go、api_test.go、comment_test.go、indent_test.go、indent_getindentation_test.go（"format 自有套件"是阶段 8 门禁项）。
- **危险点**：规则表初始化顺序 = 规则优先级（rulesmap 桶排序确定性）；依赖 ls/lsutil。
- **依赖**：ast、astnav、core、debug、ls/lsutil、scanner、stringutil。

### 8.3 internal/lsp → src/Ts.LanguageService / Lsp/ / Ts.Lsp.*

- **LOC** 21,008（其中 lsproto/lsp_generated.go 17,262 为元模型生成物）；测试 3,847。**阶段 8**。
- **文件映射**：server.go(1,883)→Server.cs（LSP 主循环/请求分发）；dynamic_queue.go→DynamicQueue.cs（请求调度，channel→System.Threading.Channels）；progress.go→Progress.cs；logger.go→Logger.cs；stack_sanitizer.go→StackSanitizer.cs（崩溃上报的栈脱敏——.NET 栈帧格式不同，逐 regex 适配）；lsproto/ → Ts.Lsp.LsProto：lsp.go(312)→Lsp.cs、baseproto.go→BaseProto.cs（Content-Length 帧）、jsonrpc.go→JsonRpc.cs、**structcodec.go(164，reflect 驱动的 struct↔JSON 编解码:34-130)→ 消失**（由 STJ 源生成上下文替代）、lsp_generated.go→Protocol.g.cs（generate-lsp.ts 从 metaModel.json 生成：record + STJ 上下文 + OR 类型手写 Converter，主计划 §一.4）；lspwatcher/ → Ts.Lsp.LspWatcher；_generate/（fetchModel.mts、generate.mts）→ tools/generator/generate-lsp.ts 的移植母本。
- **测试资产**：replay_test.go（**LSP stdio 回放集成测试**——阶段 8 门禁项）、server_completion/progress/projectinfo/projectreference_updates/semantictokens/shutdown_test.go、dynamic_queue_test.go、progress_test.go、stack_sanitizer_test.go、lsproto/lsp_json_test.go(1,036)+lsp_test.go+baseproto_test.go。
- **危险点**：reflect（structcodec）整体替换；OR/AND 联合类型的 JSON 编解码与 Go 输出逐字节对齐（回放测试货币）；请求取消语义（context→CancellationToken）。
- **依赖**：api、bundled、collections、core、diagnostics、fswatch、json（→STJ）、jsonrpc、locale、ls、pprof、project、tspath、vfs。

### 8.4 internal/project → src/Ts.LanguageService / Project/ / Ts.Project.*

- **LOC** 11,016；测试 11,603（测试>生产，全移植）。**阶段 8**。
- **关键文件**：session.go(1,837)→Session.cs；projectcollectionbuilder.go(1,259)→ProjectCollectionBuilder.cs；snapshotfs.go(802)→SnapshotFs.cs（**xxh3.HashString128 内容哈希 snapshotfs.go:394,424——纯内存，非承重**，主计划 §一.6 分级）；configfileregistrybuilder.go(733)→ConfigFileRegistryBuilder.cs；snapshot.go(562)→Snapshot.cs；checkerpool.go(530)→CheckerPool.cs；project.go(502)→Project.cs；watch.go(469)→Watch.cs；overlayfs.go(395)→OverlayFs.cs；其余（api.go、autoimport.go、client.go、compilerhost.go、configfileregistry.go、customconfig、extendedconfigcache.go、filechange.go、ownercache.go、parsecache.go、programcounter.go、projectcollection.go、refcountcache.go、snapshotfs、untitled 等）同名映射。
- **子包**：dirty/ → Ts.Project.Dirty（COW/持久化 map：box、cloneablemap、entry、map、mapbuilder、syncmap——被 ls 引用，**先于 ls 根包移植**）；ata/ → Ts.Project.Ata（自动类型获取：ata.go、discovertypings.go、installnpmpackages.go、typesmap.go、validatepackagename.go——起 npm 子进程，Process API）；background/ → Ts.Project.Background（queue.go）；logging/ → Ts.Project.Logging（logger、logcollector、logtree——被 ls 引用）。
- **测试资产**：约 20 个根包 *_test.go（session/snapshot/watch/projectcollection/checkerpool/overlayfs/refcountcache/bulkcache/configfilechanges/projectlifetime/untitled…）+ ata 全套 + dirty/syncmap_test + logging/logtree_test。
- **危险点**：快照/引用计数生命周期（refcountcache）在 GC 语义下的等价性论证（Go 手动 release → C# 保留显式 release，不改成 finalizer）；ata 子进程编排 OS 差异；watch 时序测试的确定化。
- **依赖**：ast、checker、collections、compiler、core、debug、diagnostics、json（→STJ）、locale、ls（含 autoimport/lsconv/lsutil）、lsp/lsproto、module、packagejson、parser、semver、sourcemap、tsoptions、tspath、vfs。

### 8.5 internal/fswatch → src/Ts.LanguageService / FsWatch/ / Ts.FsWatch —— **整包重写（FileSystemWatcher），非 1:1**

- **LOC** 5,446；测试 4,247。**阶段 8 本体**；**最小后端提前到阶段 7**（execute/watchmanager 生产依赖，§13.3）。
- **Go 面**：vendored 平台 watcher（含 LICENSE/cgmanifest.json——上游派生代码）：fsevents_darwin*.go + **3 个 .s 汇编 FFI 文件**、inotify_linux.go、fanotify_linux.go、kqueue.go、windows.go（ReadDirectoryChangesW）、walkdir*（平台 dirent 遍历）、debounce.go、event.go、watcher.go、canonicalize_darwin.go（NFD 规范化）。
- **C# 方案（主计划 §二 阶段 8 明示）**：保留对外 API（watcher.go 的接口、event.go 事件模型、debounce.go 去抖逻辑逐行直译），**平台后端整体换成 `System.IO.FileSystemWatcher`** + `Directory.EnumerateFileSystemEntries`（替代 walkdir）；darwin NFD 规范化语义保留（String.Normalize(NormalizationForm.FormD) 对齐 canonicalize_darwin.go）。.s/FFI/syscall 全部无对应。
- **测试资产**：watcher_test.go、walkdir_test.go、eventlist_test.go、testutil_test.go 移植到新后端上跑；fanotify_linux_test.go、fsevents_darwin_*_test.go 等**平台后端专属测试不移植**（后端已换），其行为断言折算进 watcher 级测试。
- **危险点**：FileSystemWatcher 的事件合并/丢事件语义与 Go 后端不同——12-risks.md 登记；去抖窗口与递归 watch 的目录上限（Linux inotify watch 数）；大小写不敏感 FS 的路径规范化。
- **依赖**：nativepath。

### 8.6 internal/jsonrpc → src/Ts.LanguageService / JsonRpc/ / Ts.JsonRpc

- **LOC** 287；无测试（lsproto/baseproto_test 覆盖同族帧逻辑）。**阶段 8**。baseproto.go→BaseProto.cs（Content-Length 帧读写）、jsonrpc.go→JsonRpc.cs（请求/响应/错误对象）。api 与 lsp 共用。危险点：帧头解析的字节精确性；STJ 上下文。依赖：json（→STJ）。

### 8.7 internal/fourslash → tests/Ts.Fourslash.Tests / Ts.Fourslash（测试基架）

- **LOC** 9,770（基架）；测试 771,464 行 = **289（顶层手写）+ 210（tests/manual/）+ 3,796（gen/）= 4,295** 个 `*_test.go`（R10 三段口径）。**阶段 8**。
- **文件映射**：fourslash.go→Fourslash.cs（标记文件解析/虚拟会话/断言库）；test_parser.go→TestParser.cs；baselineutil.go→BaselineUtil.cs；statebaseline.go→StateBaseline.cs；semantictokens.go→SemanticTokens.cs；skip_if_failing.go→SkipIfFailing.cs（配合 _scripts/failingTests.txt 的跳过清单机制原样保留——fourslash 分级门禁 50%→90%→99%+ 靠它记账）。
- **代码生成**：_scripts/convertFourslash.mts（把官方 TS fourslash 用例转成 Go 测试）→ 改造为发射 **C# xUnit 测试**（tools/generator/convert-fourslash.ts）；gen/ 下 3,796 个文件全部再生成而非手工转写；manualTests.txt/unparsedTests.txt/crashingTests.txt 清单一并入库。
- **危险点**：体量风险（12-risks.md：fourslash 体量）——3,796 个生成文件对 IDE/编译时间的压力，考虑分批多测试程序集；生成器输出的稳定排序。
- **依赖**：bundled、collections、compiler、core、debug、diagnostics、diagnosticwriter、execute、json（→STJ）、jsonrpc、locale、ls、lsp、modulespecifiers（测试引用）、project、repo、stringutil、testrunner、testutil、tspath、vfs。

---

## 9. 阶段 9 · API 与长尾

### 9.1 internal/api → src/Ts.LanguageService / Api/ / Ts.Api.*

- **LOC** 9,110；测试 1,199。**阶段 9**。
- **文件映射**：server.go→Server.cs；session.go→Session.cs；proto.go→Proto.cs；protocol.go→Protocol.cs；**protocol_msgpack.go→ProtocolMsgpack.cs（手写 msgpack 帧逐字节直译，零第三方依赖**，主计划 §二 阶段 9；fixed-array/fixstr 等字节码常量如 protocol_msgpack.go:32 原样）；protocol_jsonrpc.go→ProtocolJsonRpc.cs；conn.go/conn_sync.go/conn_async.go→Conn*.cs；callbackfs.go→CallbackFs.cs；timing.go→Timing.cs；**transport_unix.go/transport_windows.go→Transport.cs 单文件运行时分支**（UnixDomainSocket→`UnixDomainSocketEndPoint`，go-winio 命名管道→`NamedPipeServerStream`，主计划 §一.5 依赖映射）；encoder/ → Ts.Api.Encoder：encoder.go(844)/decoder.go(381)/stringtable.go 手写直译，encoder_generated.go(707)/decoder_generated.go(1,140) → Encoder.g.cs/Decoder.g.cs（generate-encoder.ts，04-codegen.md 第 7 族，输入 ast.json）。
- **测试资产**：proto_test.go、session_apistate_test.go、session_completion_test.go、timing_test.go、encoder/encoder_test.go+decoder_test.go（**编码器往返测试 = 阶段 9 门禁**）。
- **危险点**：二进制 AST 编码的字段序/偏移表与 ast.json 同源——生成器输入漂移即坏；OS 特定 transport；stringer_generated.go → stringer 族。
- **依赖**：ast、astnav、bundled、checker、collections、compiler、core、diagnostics、jsnum、json（→STJ）、jsonrpc、locale、ls、lsp、nodebuilder、pprof、printer、project、tsoptions、tspath、vfs。

### 9.2 internal/tracing → Ts.Compiler / Tracing/ / Ts.Tracing —— **归宿 EventSource，非 1:1**

- **LOC** 763；测试 140。**接口骨架＝阶段 4/5 前置**（compiler、checker 生产 import：checker/tracer.go:14-15、compiler/program.go）；**完整实现＝阶段 9**（主计划 §二："tracing→EventSource"）。
- **Go 面**：tracing.go——`--generateTrace` 的 Chrome trace_event JSON 写出 + 类型转储（`Tracer`/`TracedType` 接口，tracing.go:22-45：45+ 个类型数据访问器）；用 xxh3-64 做路径去重键（**非承重**，主计划 §一.6：tracing 用 XXH3-64→XxHash3）。
- **C# 方案**：`Ts.Tracing` 保留 `Tracing`/`Tracer`/`TracedType` 接口与 no-op 空实现（checker 的 `if tr != nil` 判空路径逐字直译）；事件发射端改为 `System.Diagnostics.Tracing.EventSource`（EventPipe 消费，dotnet-trace 可视化），**不复刻 Chrome trace JSON 字节**。types.json 类型转储是否需保真主计划未言明 → §13.4。
- **测试资产**：tracing_test.go（对 JSON 输出的断言部分随方案调整或降级为事件计数断言——非 1:1 区）。
- **依赖**：ast、json、scanner、tspath、vfs。

### 9.3 internal/locale → Ts.Compiler / Locale/ / Ts.Locale（微包）

- **LOC** 28（未列入任务 LOC 清单，实测；§13.1）；无测试。**阶段 9**（--locale 诊断本地化；ast/diagnostics 对它的 import 只是类型引用，阶段 3 起以骨架存在）。
- **Go 面**：locale.go——`golang.org/x/text/language` 的 BCP-47 tag 薄封装 + context 传递。
- **C# 方案**：`readonly struct Locale(string Tag)` + 手写 BCP-47 归一化匹配（对 13 个语言包做前缀匹配即可，语料闭集）；**不用 CultureInfo**——`InvariantGlobalization=true` 下 CultureInfo 数据被裁（07-aot-constraints.md）。context 传递 → 显式参数/AsyncLocal（05-conventions.md context 规则）。
- **依赖**：无 internal 依赖。

---

## 10. 特殊归宿包（消失/换体/测试专用）

### 10.1 internal/json —— **薄封装消失，不移植**

- **LOC** 100；无测试。Go 面（json.go 全文核验）：对 `github.com/go-json-experiment/json` 的再导出——Marshal*/Unmarshal* 一律默认注入 `jsontext.AllowInvalidUTF8(true)`（json.go:12-21），另有类型别名（Value/Decoder/Encoder）与 token 常量。该包同时是 `.golangci.yml:18` depguard 的豁免点（`//nolint:depguard`，json.go:1）——即 tsgo 用它把"哪种 JSON 库"收敛到一处。
- **C# 归宿**：**没有对应包**。调用点直接使用 System.Text.Json：源生成上下文 + `Utf8JsonWriter`（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`），按 01-architecture.md 的字节保真约定；`AllowInvalidUTF8(true)` 的语义对应主计划 §一.5 的「无效 UTF-8→U+FFFD」规则。Go 侧 depguard 的治理职能由 07-aot-constraints.md 的「反射序列化器封禁 + 仅源生成上下文」analyzer 门禁承接。每个原 import json 的包（core、collections、diagnostics、sourcemap、tracing、jsonrpc、lsp、packagejson、execute、api…）在各自移植时落 `XxxJsonContext` 源生成上下文，型录集中登记在 07-aot-constraints.md。
- **状态**：N/A（已消化进约定）。

### 10.2 internal/pprof —— 见 §7.2（EventPipe/诊断，非 1:1）

### 10.3 internal/repo —— **定位仓库根，测试/生成器专用**

- **LOC** 87；无测试。Go 面（paths.go 全文核验）：`runtime.Caller(0)` 取当前源文件路径向上找 `go.mod` 定仓库根（paths.go:11-40），派生 `TypeScriptSubmodulePath()`（_submodules/TypeScript）、`TestDataPath()`（testdata）、`SkipIfNoTypeScriptSubmodule()`。生产代码 import 它的只有 `//go:build ignore` 的生成器（diagnostics/generate.go、bundled/generate.go）与测试。
- **C# 归宿**：tests/Ts.TestUtil / `RepoPaths.cs`。实现：`[CallerFilePath]` 编译期常量取 RepoPaths.cs 自身路径，向上找 `TypeScript.slnx`（等价 go.mod 探针）；Go 的 `-trimpath` panic 分支（paths.go:18-20）对应 C# 的 `PathMap`/`Deterministic` 构建下路径被重写的防御——CI 中测试程序集不开路径重写即可，防御性 throw 保留。`SkipIfNoTypeScriptSubmodule` → xUnit 的 SkippableFact 等价物（08-testing-strategy.md）。**绝不进 src/ 三个产品程序集**（AOT 产物不得依赖源码路径）。
- **依赖**：无。

---

## 11. 未列入 45 包 LOC 清单的微包与仓库杂项

### 11.1 internal/debug → Ts.Compiler / Core/Debug.cs / Ts.Core（吸收为静态类）

- **LOC** 61（实测）；测试 96。**阶段 1**。debug.go→src/Ts.Compiler/Core/Debug.cs：吸收为 `Ts.Core.Debug` 静态类，**不占 41 包名额**（R3，01-architecture.md §2.3 口径）；assert/fail 助手（panic→异常，05-conventions.md）。被 core、ast、scanner 等广泛引用，地基最早一批。

### 11.2 internal/locale —— 见 §9.3。

### 11.3 internal/nodebuilder —— 见 §5.3。

### 11.4 cmd/tsgo（非 internal）→ src/Ts.Cli / Program.cs

- main.go/sys.go/lsp.go/api.go/enablevtprocessing_windows.go/isprocessalive_*.go → Ts.Cli 入口：默认 tsc / `--lsp` / `--api` 三路复用单二进制（主计划 §一.3），`PublishAot=true`。OS 特定文件（VT 处理、进程存活探测）→ 运行时分支。不在 45 包清单内，随阶段 7（tsc 路）与阶段 8/9（lsp/api 路）分步接线。

---

## 12. 依赖 DAG 备注与移植序核对

### 12.1 子包级依赖修正（防"假环"误判）

包级粗看存在 ls↔lsp、ls↔project 互引，实际在 Go 子包级无环（Go 编译器保证），装配 C# 项目内文件夹时按以下真实方向：

- `ls` 根 → 只依赖 `lsp/lsproto`（协议类型）与 `project/dirty`、`project/logging`（助手），**不依赖** lsp 服务器与 project 根；
- `lsp` 根（服务器）→ 依赖 ls、project 根；
- `project` 根 → 依赖 ls 根、ls/autoimport、ls/lsconv、ls/lsutil、lsp/lsproto；
- `format` → 依赖 `ls/lsutil`（非 ls 根）。

因此 Ts.LanguageService 内部文件夹移植序：LsProto(g) → Project.Dirty/Logging → Ls.LsUtil/LsConv → Format → Ls 根/AutoImport/Change → Project 根/Ata/Background → Lsp 服务器。C# 同一程序集内命名空间无环约束，但**保持该序可使每步可编译可测**。

### 12.2 生产无环的两处易误判

- checker **不**依赖 compiler（仅 checker_test.go 引用）；compiler→checker 单向。
- printer **不**依赖 checker（经 nodebuilder 接口解耦）；transformers→checker 单向（declarations 变换直用 checker）。

---

## 13. 待澄清（呈报主计划维护者，不擅改设计）

1. **「41 包」计数与实际不符**：任务/主计划口径为 41 包，但核实 LOC 清单含 45 个包；`internal/` 实际有 48 个目录——debug(61)、locale(28)、nodebuilder(78) 三个微包不在 LOC 清单中但被生产代码依赖（checker→nodebuilder、ast→locale、全域→debug）。本文档按 48 目录全覆盖处理（§11），请主计划统一计数口径。**【部分关闭（R3）：debug 已裁定吸收为 src/Ts.Compiler/Core/Debug.cs 的 `Ts.Core.Debug` 静态类，不占 41 包名额（§11.1 已改）；locale/nodebuilder 的计数口径仍待统一。】**
2. **execute 的项目归属未在主计划 §一.3 指定**：Ts.Compiler 的文件夹清单无 Execute/，而 execute/tsc.go:16,19 生产依赖 format 与 ls/lsutil（属 Ts.LanguageService）——execute 放 Ts.Compiler 会造成反向引用。本文档暂定 **Ts.Cli**（§7.1），需主计划确认；同时阶段 7（tsc CLI）将提前用到 ls/lsutil + format 两个阶段 8 包的子集，09-phases.md（补写中）的重叠 DAG 应显式登记。**【部分关闭（R18）：tests/ 五个核心项目不变，execute/tsctests 归 Ts.Conformance.Tests、不设 Ts.Cli.Tests（§7.1 已改）；execute 生产代码归 Ts.Cli 的暂定仍待确认。】**
3. **fswatch 阶段归属矛盾**：主计划 §二把 fswatch 列在阶段 8，但阶段 7 的 execute（--watch/--build watch）经 watchmanager/watchbackend.go 生产依赖 fswatch。本文档按「最小后端提前阶段 7、整包重写归阶段 8」处理（§8.5），请确认。
4. **tracing→EventSource 后 --generateTrace 的输出契约**：Chrome trace JSON 与 types.json 是否有下游工具/基线依赖需要字节保真？主计划仅定"EventSource 方案"。若 conformance/性能工作流不消费该输出，本文档的非 1:1 处理成立；否则需补一条保真通道。
5. **checker.go 无分节注释**：主计划 §一.3 称"按 Go 原有分节注释拆 partial class"，实测 checker.go 无任何 banner 注释（1,484 个顶层函数）。**【已关闭（R1）：拆分表唯一权威 = 03-checker-design.md §4.2 的 22 文件表（函数簇锚点、函数为原子、连续行范围）；§5.1 已删自有分表，改为镜像 03 的文件名清单。】**
6. **fourslash 生成测试的转换器所有权**：convertFourslash.mts 需改写为发射 C#（3,796 个生成用例与 289+210 个手写用例的移植路径不同；计数口径已按 R10 统一为 289+210+3,796=4,295），主计划 §三 只在 08-testing-strategy.md 使命中提"fourslash 策略"——生成器归属（04-codegen.md 第八族？）与手写用例的转写方式需主计划定夺。
