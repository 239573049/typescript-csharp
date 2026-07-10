# 10 — 阶段 0（脚手架）与阶段 1（地基）：文件级 WBS（滚动波详规）

| 元信息 | |
|---|---|
| 版本 | v1.1 |
| 日期 | 2026-07-11 |
| 状态 | Draft |
| 主计划 | `~/.claude/plans/precious-riding-hoare.md`（已批准；本文档是其 §三 文档集中的 10 号） |
| 上游快照 | `/Users/token/Desktop/code/typescript-go-main`（tsgo，`core.Version()` = `"7.1.0-dev"`，internal/core/version.go:8） |
| 目标仓库 | `/Users/token/Desktop/code/typescript-csharp` |
| 交叉引用 | 00-overview.md（钉扎/testdata）、01-architecture.md（线程/内存/布局）、02-ast-design.md（AST 设计）、04-codegen.md（生成器权威）、05-conventions.md（惯用法）、06-module-inventory.md（逐包清单）、07-aot-constraints.md（props/analyzer 权威）、08-testing-strategy.md（夹具/跑批器）、09-phases.md（全阶段门禁） |

---

## 0. 本文范围与使用方式

本文是**滚动波（rolling wave）详规**：只对「当前阶段（0 脚手架）+ 下一阶段（1 地基）」给出文件级 WBS；阶段 2 及以后仅在 §3 给一段梗概并指向 09-phases.md。每次阶段门禁签核后，本文滚动改版：把已完阶段折叠为签核记录，为新的「下一阶段」展开文件级详规。

**状态图例**（对 2026-07-11 仓库实况逐文件核对）：

- ✅ 已存在且符合规范（无需动作）
- ⚠️ 已存在但需收敛（与权威文档有差异，动作 = 按差异表修改）
- ☐ 待建

**当前实况快照（2026-07-11 实施校准）**：目标仓库已在 `main` 建立初始提交；W0-01～04、06～14、21 已落地。TypeScript 子模块钉在 `4d4f005c…`，`UPSTREAM.lock` 钉 tsgo `168e7015…`；testdata 的 49,642 个 Git 跟踪文件语义镜像已入库并由 `.gitattributes` 禁止换行归一化；AOT/Trim/SingleFile/globalization/Banned API 约束已全仓启用。当前主线转入 W0-15～18 生成器自举，W0-05/19/20/22 仍待完成或验收。

---

## 1. 阶段 0 WBS——脚手架

### 1.1 交付物总清单

| # | 交付物 | 路径 | 状态 | 验收方式 |
|---|---|---|---|---|
| W0-01 | git init + 首次提交 | 仓库根 | ✅ | `git log` 有初始提交；`git status` 无 obj/bin 噪声 |
| W0-02 | .gitignore | `/.gitignore` | ✅ | §1.7 全文落库 |
| W0-03 | TypeScript 子模块钉扎 | `/_submodules/TypeScript` | ✅ | `git submodule status` 无 `+`/`-` 前缀；SHA == UPSTREAM.lock |
| W0-04 | UPSTREAM.lock | `/UPSTREAM.lock` | ✅ | 五项指纹齐全（00-overview.md §4.3） |
| W0-05 | tools/check-upstream-pin.sh | `/tools/check-upstream-pin.sh` | ☐ | 本地跑通；CI 首步接入 |
| W0-06 | tools/sync-testdata.sh + testdata 初次同步 | `/tools/sync-testdata.sh`、`/testdata/` | ✅ | baselines/reference Git 跟踪文件数 49,321 + 镜像总计数/逻辑字节入 lock（R14；00-overview.md §5.1） |
| W0-07 | TypeScript.slnx | `/TypeScript.slnx` | ✅ | `dotnet build TypeScript.slnx` 绿 |
| W0-08 | Directory.Build.props ×3 | 根 / src/ / tests/ | ✅ | 收敛到 07-aot-constraints.md §3 全文（差异表 §1.4） |
| W0-09 | Directory.Packages.props | 根 | ✅ | §1.5 逐包表；补 BannedApiAnalyzers |
| W0-10 | BannedSymbols.txt | 根 | ✅ | 07-aot-constraints.md §1 的机器可读镜像 |
| W0-11 | .editorconfig | 根 | ✅ | §1.6 要点齐全；`dotnet build` 后 analyzer severity 生效 |
| W0-12 | 8 个 csproj | src/ ×3、tests/ ×5 | ✅ | §1.8 关键内容 + 收敛项 |
| W0-13 | Program.cs 最小实现 | `src/Ts.Cli/Program.cs` | ✅ | `tscs --version` 输出 `Version 7.1.0-dev` |
| W0-14 | LargeStackExecutor.cs + 单测 | `src/Ts.Compiler/Core/LargeStackExecutor.cs`、`tests/Ts.Compiler.Tests/Core/LargeStackExecutorTests.cs` | ✅ | §1.10 API 核对；单测绿 |
| W0-15 | tools/generator（AST/诊断/libs 自举） | `/tools/generator/` | ☐ | §1.11；drift 校验（生成后 `git diff --exit-code`） |
| W0-16 | Ast/*.g.cs ×7 + 最小手写支撑面 | `src/Ts.Compiler/Ast/` | ☐ | 生成入库（R2 定稿 7 文件，02 §16）；analyzer 零告警 |
| W0-17 | Diagnostics/Messages.g.cs | `src/Ts.Compiler/Diagnostics/` | ☐ | 单测断言计数 == 2,154 |
| W0-18 | Bundled/libs ×108 EmbeddedResource | `src/Ts.Compiler/Bundled/` | ☐ | 单测断言资源计数 == 108 |
| W0-19 | Tracing 接口占位 | `src/Ts.Compiler/Tracing/` | ☐ | 空接口/空实现编译过（01-architecture.md §2.3：接口占位在阶段 0） |
| W0-20 | CI 工作流 | `/.github/workflows/ci.yml` | ☐ | §1.13 草案；三平台矩阵全绿 |
| W0-21 | plan/ 13 份文档 | `/plan/` | ✅ | 00–12 齐全（本文即 10 号） |
| W0-22 | tools/dumptool 骨架 + fixtures.lock.json 初版 | `/tools/dumptool/patches/`、`/tools/dumptool/regen.sh`、`/fixtures/fixtures.lock.json` | ☐ | R16：阶段 0 交付物；规格 = 08-testing-strategy.md §2；patches/README.md + regen.sh 骨架落库，fixtures.lock.json 的 tsgoCommit == 坐标 A；夹具存储通道决策进 §1.14 出口 checklist |

### 1.2 git init 与上游钉扎步骤（W0-01/03/04/05）

**顺序敏感**：.gitignore 必须在 `git add` 之前落库，否则 obj/ 会进暂存区。

```bash
cd /Users/token/Desktop/code/typescript-csharp

# 1. 初始化（默认分支 main）
git init -b main

# 2. 先落 .gitignore（§1.7 全文），再首提
git add .gitignore
git add TypeScript.slnx Directory.Build.props Directory.Packages.props src tests plan README.md
git commit -m "phase 0: solution scaffolding"

# 3. 查询坐标 B（TypeScript 子模块 pin）——两法，权威流程见 00-overview.md §4.2
# 方法 1（要求 tsgo 是 git 检出；当前快照会失败，见下）：
git -C /Users/token/Desktop/code/typescript-go-main ls-tree HEAD _submodules/TypeScript
#   → 160000 commit <SHA>  _submodules/TypeScript     ← <SHA> 即权威 pin
# 或人类可读版：
git -C /Users/token/Desktop/code/typescript-go-main submodule status
# 方法 2（GitHub API，无需本地 git 元数据）：
curl -s "https://api.github.com/repos/microsoft/typescript-go/contents/_submodules/TypeScript?ref=<tsgo-commit>" \
  | jq -r '.sha'

# 4. 添加子模块并钉扎到坐标 B
git submodule add https://github.com/microsoft/TypeScript.git _submodules/TypeScript
git -C _submodules/TypeScript fetch --depth 1 origin <坐标B-SHA>
git -C _submodules/TypeScript checkout <坐标B-SHA>
git add .gitmodules _submodules/TypeScript

# 5. 写 UPSTREAM.lock（坐标 A/B、testdata 指纹、生成器输入 sha256）并提交
git add UPSTREAM.lock && git commit -m "phase 0: pin upstream"
```

**本地实况（已实测，2026-07-11）**：`/Users/token/Desktop/code/typescript-go-main` 不是 git 检出——方法 1 两条命令均报 `fatal: not a git repository`，且其 `_submodules/TypeScript/` 目录为空。方法 2 需要 `<tsgo-commit>`（坐标 A），而 zip 快照无法自证坐标 A。**处置**（与 00-overview.md §4.2/§10-1 一致）：阶段 0 内用 `git clone` 到固定 commit 替换 zip 快照（同时对两个上游各留完整 clone 归档，00-overview.md §4.4-C），然后方法 1 直接可用。在坐标 A 确定前，W0-03/W0-04 不能签核——列 §4 待澄清第 1 条。

**UPSTREAM.lock 格式**（本文档钉死，校验脚本按此解析；生成器输入副本所有权见 04-codegen.md）：

```
# UPSTREAM.lock — 单一事实源；只能由升级原子 PR（00-overview.md §4.3）修改
tsgo.commit        = <坐标A-SHA>
tsgo.source        = https://github.com/microsoft/typescript-go （clone 归档：<路径>）
typescript.commit  = <坐标B-SHA>
testdata.files.reference = 49321          # testdata/baselines/reference/ Git 跟踪文件（验收货币，00-overview.md §5.1）
testdata.files.total     = <find 实测>     # testdata/ 镜像全目录（R14 双字段；自有夹具在仓库根 fixtures/，由 fixtures.lock.json 另行管理）
testdata.bytes     = <普通文件 wc -c 逻辑长度之和>  # 禁用 du：其报告文件系统块占用且平台选项不一致
sha256.ast.json            = <...>
sha256.schema.ts           = <...>
sha256.diagnosticMessages.json      = <...>   # 取自 _submodules/TypeScript/src/compiler/
sha256.extraDiagnosticMessages.json = <...>   # 取自 tsgo internal/diagnostics/
sha256.metaModel.json      = <...>            # 阶段 8 前占位可空
```

**tools/check-upstream-pin.sh**（W0-05）依次校验五项（00-overview.md §4.4 漂移定义）：① submodule gitlink == lock；② `git submodule status` 无 `+`/`U`；③ testdata 镜像总文件数、baselines/reference 文件数与字节数三项 == lock（R14 双字段口径）；④ 生成器输入副本 sha256 == lock；⑤ 重跑生成器后 `git diff --exit-code -- 'src/**/*.g.cs'` 为空。任一失败退出码非 0，CI 红灯。

### 1.3 TypeScript.slnx（W0-07，✅）

现有文件即目标形态：`/src/` 文件夹含 Ts.Compiler / Ts.LanguageService / Ts.Cli 三项目，`/tests/` 文件夹含 Ts.TestUtil / Ts.Compiler.Tests / Ts.Conformance.Tests / Ts.LanguageService.Tests / Ts.Fourslash.Tests 五项目。tools/generator 是 Node 工程、plan/ 是文档，都不进 slnx。无收敛项。注（R18）：五个核心测试项目不变；execute/tsctests 移植归入 Ts.Conformance.Tests（不设 Ts.Cli.Tests）；Ts.Conformance.AotHost 与 Ts.Benchmarks 为合法后期追加，各自阶段建成时再加入 slnx（08-testing-strategy.md §1 的 E/F 类宿主）。

### 1.4 Directory.Build.props 三层（W0-08，⚠️）

**权威全文在 07-aot-constraints.md §3.2–3.4，本节只列「当前仓库 → 权威文本」的收敛差异**（执行时逐条打勾）：

| 层 | 差异 | 动作 |
|---|---|---|
| 根 | `LangVersion` 当前 `latest`，权威 `14.0` | 改为 `14.0`（生成代码可重现编译，07 §3.6） |
| 根 | 缺 `JsonSerializerIsReflectionEnabledByDefault=false` | 补（STJ 反射序列化器运行期一票否决） |
| 根 | 缺 `EnableTrimAnalyzer/EnableAotAnalyzer/EnableSingleFileAnalyzer`（当前只在 src 层） | 上提到根——**测试项目也要三 analyzer**（07 §3.2 注释） |
| 根 | 缺 `AnalysisModeGlobalization=All`、`EnableNETAnalyzers`、`CodeAnalysisTreatWarningsAsErrors` | 补 |
| 根 | 缺 BannedSymbols.txt 的 `AdditionalFiles` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` 引用 | 补（W0-10 一并落库） |
| 根 | 多出 `TsVersion` 属性（07 全文没有） | **保留**——它是 Version.cs 与 CI 校验的单一事实源，向 07 提修订把它并入全文（§4 待澄清第 6 条） |
| src | 当前手写 `WarningsAsErrors` CA1304–1311 列表 | 可保留为双保险；权威机制是根层 `AnalysisModeGlobalization=All` + .editorconfig severity（07 §7.4） |
| tests | 缺 `IsAotCompatible=false` 显式声明 | 补（07 §3.4） |
| tests | `OutputType=Exe` 写在层级 props，Ts.TestUtil 再覆盖回 Library | 建议下放到四个测试 csproj，TestUtil 不再反向覆盖（纯整洁性，非门禁项） |

逐属性理由**不在本文重复**——见 07-aot-constraints.md §3.6 的完整表。

### 1.5 Directory.Packages.props（W0-09，⚠️）

| 包 | 版本 | 用途 | 状态 |
|---|---|---|---|
| System.IO.Hashing | 10.0.0 | XxHash128/XxHash3（xxh3 映射，主计划 §一.5；阶段 4+ 才有调用点，先钉版本） | ✅ 已列 |
| xunit.v3 | 3.1.0 | 测试框架（主计划 §一.3） | ✅ 已列 |
| xunit.runner.visualstudio | 3.1.5 | VS/`dotnet test` 适配 | ✅ 已列 |
| Microsoft.NET.Test.Sdk | 17.14.1 | 测试宿主 | ✅ 已列 |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 落地当日最新稳定（写入即钉死） | BannedSymbols.txt 强制（07 §3.2） | ☐ 待补 |

**注**：主计划 §一.3 例示的 `System.Threading.Channels` 在 net10.0 是**框架内置程序集**（Microsoft.NETCore.App 自带），无需包版本行——按内置处理，不列包（§4 待澄清第 4 条备案）。新增包的铁律：先查 07-aot-constraints.md §2 允许清单，AOT 不兼容者一票否决。

### 1.6 .editorconfig（W0-11，☐）——C# 风格规则要点

完整文件由本节要点 + 07-aot-constraints.md §7.4 的 globalization severity 片段拼装：

1. `root = true`；**只写 `[*.cs]`/`[*.csproj]`/`[*.props]` 节**。禁止在 `[*]` 上设 `trim_trailing_whitespace`/`insert_final_newline`——`testdata/` 基线文件（.js/.errors.txt/…）一个字节都不能被编辑器碰（00-overview.md §5.2 只读纪律）。
2. `[*.cs]`：`charset = utf-8`、`indent_style = space`、`indent_size = 4`、`end_of_line = lf`（配 `.gitattributes` 的 `*.cs text eol=lf` 双保险）。
3. 风格规则（与 05-conventions.md 对齐，冲突以 05 为准）：
   - `csharp_style_namespace_declarations = file_scoped:error`（全仓 file-scoped，现有文件已是）；
   - `csharp_using_directive_placement = outside_namespace:error`（对照 Go import 块置于文件头）；
   - 命名规则：类型/公有成员 PascalCase = error；**接口 I 前缀规则降为 suggestion**——13 个数据 mixin 接口刻意不加 I 前缀保 grep 对照性（主计划 §一.1，例外记录在 05-conventions.md）；
   - `dotnet_style_require_accessibility_modifiers = always:error`。
4. `[*.g.cs]`：`generated_code = true` + 关闭 IDE 风格诊断（`dotnet_diagnostic.IDE*.severity = none`）。**不得**豁免 IL/CA 诊断——阶段 0 门禁就是「Ast/*.g.cs 过 AOT+Trim analyzer」。
5. globalization 片段（CA1304/1305/1307/1309/1310/1311 = error）：照抄 07-aot-constraints.md §7.4。

### 1.7 .gitignore（W0-02，☐）全文

```gitignore
# .NET
bin/
obj/
artifacts/
*.binlog
*.user

# IDE
.vs/
.idea/
.vscode/
*.DotSettings

# Node（生成器）
node_modules/

# 一致性跑批器的本地输出（reference/ 必须入库，local/ 绝不入库——00-overview.md §5.2）
testdata/baselines/local/

# 系统
.DS_Store
```

### 1.8 八个 csproj 关键内容（W0-12，⚠️）

| csproj | 关键内容（已核对现状） | 收敛项 |
|---|---|---|
| src/Ts.Compiler | `RootNamespace=Ts`；后续阶段追加 `EmbeddedResource`（Bundled libs，07 §5.2 的 LogicalName 规范） | 无 |
| src/Ts.LanguageService | `RootNamespace=Ts`；ProjectReference → Ts.Compiler | 无 |
| src/Ts.Cli | `OutputType=Exe`、`AssemblyName=tscs`、`RootNamespace=Ts.Cli`、`PublishAot=true`；引用 Compiler+LanguageService | ① 补 `StackTraceSupport=true`（07 §3.5）；② 删 `OptimizationPreference=Speed`——07 §3.5 明言 OptimizationPreference/ServerGC/StripSymbols 调优旋钮初始不设（归阶段 10；R4，实际 csproj 已照此修正，此项可勾销） |
| tests/Ts.TestUtil | Library；引用 Ts.Compiler；testutil/repo 移植宿主（01 §2.3 测试资产段） | 见 §1.4 tests 层 OutputType 项 |
| tests/Ts.Compiler.Tests | 引用 Ts.Compiler + Ts.TestUtil；阶段 1 所有移植单测的宿主 | 无 |
| tests/Ts.Conformance.Tests | 引用 Ts.Compiler + Ts.TestUtil；testrunner 移植宿主（阶段 3 起实装） | 无 |
| tests/Ts.LanguageService.Tests | 引用 Ts.LanguageService + Ts.TestUtil | 无 |
| tests/Ts.Fourslash.Tests | 引用 Ts.LanguageService + Ts.TestUtil；阶段 8 前保持空壳 | 无 |

### 1.9 src/Ts.Cli/Program.cs 最小实现（W0-13，✅）

现有实现已达标，核对要点：

- **分派结构**镜像 cmd/tsgo/main.go:20-31：`--lsp`/`--api` 保留分支、返回未实现错误码 1；默认路走 tsc CLI（阶段 7 实装 `execute.CommandLine` 等价物）。
- `--version`/`-v` 输出 **`Version 7.1.0-dev`**——逐字对齐 tsgo：`PrintVersion` = `diagnostics.Version_0`（"Version {0}"）+ `core.Version()`（internal/execute/tsc/help.go:15-17）。版本字符串来源 `Ts.Core.TsVersion.Value`，与 MSBuild `TsVersion` 属性双源一致性由 CI 校验（W0-20 备注）。
- 阶段 7 前**不要**给默认路加任何行为——空跑输出一行占位说明并返回 0 即现状。
- 阶段 1 workgroup 落地后，Main 主体按 01-architecture.md §4.6 E6 规则包进 `LargeStackExecutor.Run()`（阶段 0 尚无深递归，暂不必）。

### 1.10 Ts.Compiler/Core/LargeStackExecutor.cs（W0-14，✅）——完整 API 设计

**阶段 0 形态**（已落库并有单测，满足门禁）：

```csharp
namespace Ts.Core;

public static class LargeStackExecutor
{
    public const int StackSizeBytes = 64 * 1024 * 1024;   // ① 栈大小常量

    [ThreadStatic] private static bool t_onLargeStack;     // ② 重入检测

    public static void Run(Action action);                 // ③ 便捷无返回值重载
    public static T Run<T>(Func<T> func);                  // ④ 核心入口
}
```

逐要素规格（与现实现逐行对应，src/Ts.Compiler/Core/LargeStackExecutor.cs:16-62）：

1. **栈大小 64MB**：`StackSizeBytes = 64 * 1024 * 1024`。这是 tsc.js `--stack-size` 的对应物（主计划 §一.5「0 号风险」）。64MB 是虚拟保留，物理页按触碰提交，闲置代价近零（01 §4.6）。
2. **专用线程创建**：`new Thread(body, StackSizeBytes) { IsBackground = true, Name = "ts-large-stack" }` + `Start()`/`Join()`。用 `Thread(ThreadStart, maxStackSize)` 构造器重载——这是 .NET 唯一能指定栈尺寸的入口（线程池与 Task.Run 都不行，见 01 §4.5 红线 4）。
3. **重入内联**：`[ThreadStatic] t_onLargeStack` 置位后，嵌套 `Run` 直接内联执行 `func()`——避免大栈线程上再叠线程（checker→bind 嵌套是常态，01 §4.6）。
4. **异常传播**：任务体 `catch (Exception ex)` 存 `ExceptionDispatchInfo.Capture(ex)`，`Join()` 后 `failure?.Throw()`——**保留原始栈帧**重抛，对应 Go panic 冒泡语义（05-conventions.md panic→异常总则）。
5. **返回值**：`T result = default!` 闭包捕获回传。

**与 WorkGroup 的关系**（承重，勿走样）：

- `core.WorkGroup`（internal/core/workgroup.go:11-89）在阶段 1 移植为 `IWorkGroup` 双实现（01 §4.5）：`ParallelWorkGroup` 的 `Queue` **必须**把任务派发到 LargeStackExecutor 的大栈线程（对应「立即起 goroutine」且 goroutine 栈可增长）；`SingleThreadedWorkGroup.RunAndWait` 在当前线程内联执行——前提是入口已在大栈线程（E 规则，01 §4.6 入口清单 E1–E7）。
- **禁止** `Task.Run`/默认线程池承载 WorkGroup 任务（~1MB 栈必炸）；这是全仓 code review 红线。
- **演进路径**：阶段 0 的 static 简版是 thread-per-call；阶段 1 移植 workgroup.go 时按 01-architecture.md §4.6 升级为池化实例设计（`Default` 单例、`Schedule`（≈ `go fn()`）、`RunAsync`、RunAndWait 内联帮工防死锁、防饥饿增长、`TSGO_CS_STACK_SIZE` 覆盖）。**`Run<T>` 的公共语义保持不变**，调用方零改动。列 §4 待澄清第 3 条备案。

单测现状（tests/Ts.Compiler.Tests/Core/LargeStackExecutorTests.cs）：深递归成功路径（金丝雀初版）、异常传播、重入内联三类断言。阶段 3 起金丝雀升级为真实语料（超深二元表达式链，01 §4.6 验证钩子）。

### 1.11 tools/generator——生成器自举（W0-15/16/17/18，☐）

主计划阶段 0 范围明确含「生成器自举（ast+diagnostics+libs）」。**04-codegen.md 是每族输入/产物/再生成流程的权威，工作区布局与 npm scripts 以 04 §1 为唯一权威（R13）**；本节只给阶段 0 视角的文件级 WBS 摘录。

**目录结构**（平铺布局，无 input/lib/emit 子目录——04 §1.1）：

```
tools/generator/
  package.json                      # 逐族 npm scripts，见下（04 §1.2）
  package-lock.json                 # 锁定入仓
  tsconfig.json                     # 移植 tsgo _scripts/tsconfig.json
  ast.json                          # 上游 _scripts/ast.json 逐字节副本（5,487 行；sha256 入 lock）
  ast.schema.json                   # 上游 _scripts/ast.schema.json 逐字节副本
  schema.ts                         # 上游 _scripts/schema.ts 副本（1,267 行；原样复用，零修改）
  extraDiagnosticMessages.json      # tsgo internal/diagnostics/ 副本
  stringers.json                    # stringer 族声明清单（04 §7.3；11 个枚举，接入时机归 04）
  expected-counts.json              # 计数断言单一事实源：{ diagnostics: 2154, libs: 108, locales: 13, astNodes: 192, astBases: 35 }
  csharp-emit.ts                    # 公共层：CodeWriter/csIdentifier/csStringLiteral/writeIfChanged（04 §1.3；各脚本不得内嵌 writer 变体）
  csharp-names.ts                   # Go 名→C# 惯例名（getter→属性等；规则表 = 05-conventions.md，JSDoc 专名原样保留，R6）
  classify.ts                       # base 三分类：Node 系 18 / 数据 mixin 13 / brand 4
  toposort.ts                       # 继承链拓扑排序（基类先于派生类发射）
  generate-csharp-ast.ts            # 入口编排 → src/Ts.Compiler/Ast/*.g.cs ×7（R2 清单见下）
  generate-diagnostics.ts           # → src/Ts.Compiler/Diagnostics/Messages.g.cs
  generate-libs.ts                  # → src/Ts.Compiler/Bundled/libs/*.d.ts ×108 + LibNames.g.cs（04 §4）
  generate-lsp.ts                   # 第 0 天起空壳（阶段 8 实装）
  generate-stringers.ts             # 第 0 天起空壳（接入时实装）
  generate-csharp-encoder.ts        # 第 0 天起空壳（阶段 9 实装）
```

（metaModel.json / fetch-lsp-model.ts / transcribe-unicode.ts 等 LSP 与 Unicode 资产的入位时点与布局同样以 04 §1.1 为准，此处从略。）

**package.json**（逐族 npm scripts；全文以 04 §1.2 为准——node >= 22.11、typescript ^5.8.0、`--experimental-strip-types` 直跑 .ts 的零构建哲学）：

```json
{
  "private": true,
  "name": "typescript-csharp-generator",
  "type": "module",
  "engines": { "node": ">=22.11" },
  "devDependencies": {
    "@types/node": "latest",
    "typescript": "^5.8.0",
    "fast-xml-parser": "5.0.9"
  },
  "scripts": {
    "gen:ast":         "node --experimental-strip-types generate-csharp-ast.ts",
    "gen:diagnostics": "node --experimental-strip-types generate-diagnostics.ts",
    "gen:libs":        "node --experimental-strip-types generate-libs.ts",
    "gen:lsp":         "node --experimental-strip-types generate-lsp.ts",
    "gen:stringers":   "node --experimental-strip-types generate-stringers.ts",
    "gen:encoder":     "node --experimental-strip-types generate-csharp-encoder.ts",
    "gen:all":         "npm run gen:ast && npm run gen:diagnostics && npm run gen:libs && npm run gen:lsp && npm run gen:stringers && npm run gen:encoder",
    "check":           "tsc -p tsconfig.json"
  }
}
```

阶段 0 只需 `gen:ast`/`gen:diagnostics`/`gen:libs` 实装；`gen:lsp`/`gen:encoder`/`gen:stringers` 第 0 天起以空壳存在（`process.exit(0)` 并打印 "not yet implemented"），保证 `gen:all` 与 CI 新鲜度检查从第 0 天起可全量运行（04 §1.2）。`gen:lsp:fetch`/`gen:unicode` 两脚本不进 `gen:all`，本节从略（04 §1.2）。

**generate-csharp-ast.ts 骨架**（移植 1,028 行的 `_scripts/generate-go-ast.ts`；其函数分节——generateHeader/generateStructDef/generateBaseStructDefs/generateAsCast/generateNewFactory/generateUpdateFactory/generateForEachChild/generateVisitEachChild/generateClone/generateIsFunction/generateKind（generate-go-ast.ts:122-1006）——重组为七个发射面，产物 = R2 定稿的 7 个物理文件）：

```
main():
  1. 读 ast.json，经 schema.ts 的类型面校验（原样复用其 NodeType/MemberInfo）
  2. classify.ts —— 三条承重规则（主计划 §一.1，✅已对 35 base + 192 节点核验）：
     R1 基类选取 = extends 列表中最派生的 Node 系 base
        （4 个冗余重列祖先的节点丢弃 TypeNodeBase 项：UnionTypeNode/IntersectionTypeNode
         → UnionOrIntersectionTypeNodeBase；FunctionTypeNode/ConstructorTypeNode
         → FunctionOrConstructorTypeNodeBase；这两个 base 是链上抽象类，不是接口）
     R2 mixin 扁平化按字段名对传递闭包去重（DeclarationBase 13 处菱形）
     R3 无字段 brand base（TypeSyntaxBase/TypeElementBase/ClassElementBase/
        ObjectLiteralElementBase）→ 纯标记接口
  3. toposort.ts —— 按继承边排序，保证发射顺序确定（产物 diff 稳定）
  4. 依次跑七个发射面 → 写盘 7 个 .g.cs（R2 定稿，02 §16 方案胜出）：
     Nodes.g.cs / NodeCasts.g.cs / NodeFactory.g.cs / Visitors.g.cs /
     Accessors.g.cs / SyntaxKind.g.cs / GlobalUsings.g.cs
     （文件头打输入 sha256 与再生成命令注释；末尾断言节点/base/kind 计数
      == expected-counts.json，任一不符即失败——04 §2.5）
  5. 产物入库提交（不用 Roslyn 源生成器——主计划 §一.1 已裁决及理由）
```

**generate-diagnostics.ts**：输入 = `_submodules/TypeScript/src/compiler/diagnosticMessages.json`（子模块，键序）+ `extraDiagnosticMessages.json`（tsgo 自增，入仓副本）；处理管线对齐 tsgo internal/diagnostics/generate.go:84-135（合并→按 code 排序→键唯一性校验）；产物 `Messages.g.cs` 为 2,154 个 `static readonly Message` 字段（✅已核验：tsgo diagnostics_generated.go 恰有 2,154 个 var）。生成端断言总数 == expected-counts.json 的 `diagnostics`（04 §3.2）；计数断言另写成 xUnit 单测（W0-17 验收）。

**generate-libs.ts**（原 copy-libs.ts 命名废弃，R13；04 §4 为权威）：输入 = 子模块 `_submodules/TypeScript/src/lib/`（libs.json 驱动：stripJSONC 同款正则、CopyrightNotice 合成、按 target ordinal 排序——generate.go 同构，04 §4.1）；产物 = `src/Ts.Compiler/Bundled/libs/*.d.ts` ×108（与 tsgo `internal/bundled/libs/` 逐字节相同，✅实测 108 个）+ `Bundled/LibNames.g.cs`；生成端断言计数 == expected-counts.json 的 `libs`。csproj 以 `EmbeddedResource` glob + LogicalName 规范收编（07 §5.2）；`bundled:///` 覆盖 VFS 方案见 01 §9。升级 pin 时由 04-codegen.md 的同步流程重跑核对。

**W0-16 的最小手写支撑面**：Ast/*.g.cs 要过编译与 analyzer，需要少量手写占位——`Ast/Node.cs`（Kind/Flags/Loc/id/Parent 五字段基类，主计划 §一.1 的形状）、`Ast/NodeList.cs`、`NodeFlags`/`ModifierFlags` 等枚举壳（源：internal/ast/nodeflags.go 等）。**这是自举的最小代价，不是提前做阶段 3**：设计权威在 02-ast-design.md，ast 包手写半区（utilities.go 等 ~10k 行）仍归阶段 3。列 §4 待澄清第 2 条。

### 1.12 testdata 同步脚本（W0-06，☐）

`tools/sync-testdata.sh` 全文（机制与时机的权威在 00-overview.md §5.2）：

```bash
#!/usr/bin/env bash
set -euo pipefail
TSGO="${TSGO_ROOT:-/Users/token/Desktop/code/typescript-go-main}"
DEST="$(cd "$(dirname "$0")/.." && pwd)/testdata"

# 整目录镜像（含删除）。testdata/ 是 tsgo 纯镜像（R14：自有夹具一律在仓库根 fixtures/，
# 无需 exclude fixtures）；baselines/local/ 是我方跑批瞬态输出（上游同名目录同样 gitignore），
# 排除以免被 --delete 清掉
rsync -a --delete --exclude 'baselines/local/' --exclude '.DS_Store' "$TSGO/testdata/" "$DEST/"

# 指纹（写入/核对 UPSTREAM.lock；R14 双字段）
total=$(find "$DEST" -type f | wc -l | tr -d ' ')
refs=$(find "$DEST/baselines/reference" -type f | wc -l | tr -d ' ')
bytes=$(find "$DEST" -type f -exec sh -c 'for file do wc -c < "$file"; done' sh {} + | awk '{ total += $1 } END { printf "%.0f\n", total }')
echo "testdata.files.total = $total"
echo "testdata.files.reference = $refs"
echo "testdata.bytes = $bytes"
```

验收：初次同步后 `testdata/baselines/reference/` Git 跟踪文件数 49,321、镜像总文件数 49,642、普通文件逻辑字节和 147,111,511——三字段分别写入 UPSTREAM.lock（R14）；`baselines/local/` 在 .gitignore（§1.7），Finder `.DS_Store` 必须排除。差分夹具与 jsnum 夹具不在本目录（仓库根 `fixtures/`，R14）。

### 1.13 CI 工作流草案（W0-20，☐）——`.github/workflows/ci.yml`

```yaml
name: CI
on:
  push: { branches: [main] }
  pull_request:

env:
  DOTNET_NOLOGO: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true

jobs:
  # 门 1：上游钉扎与生成物漂移（00-overview.md §4.4 的五项校验）
  pin-and-drift:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with: { submodules: recursive, fetch-depth: 1 }
      - uses: actions/setup-node@v4
        with: { node-version: 24 }
      - run: bash tools/check-upstream-pin.sh   # 含重跑生成器 + git diff --exit-code '**/*.g.cs'

  # 门 2：build + analyzer + 单测（三平台矩阵）
  build-test:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest, macos-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
        with: { submodules: recursive, fetch-depth: 1 }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      # TreatWarningsAsErrors + 三 analyzer 全开 ⇒ build 绿即 analyzer 门禁过
      - run: dotnet build TypeScript.slnx -c Release
      - run: dotnet test TypeScript.slnx -c Release --no-build

  # 门 3：Native AOT 冒烟（三平台，发布真二进制并执行）
  aot-smoke:
    strategy:
      fail-fast: false
      matrix:
        include:
          - { os: ubuntu-latest,  rid: linux-x64 }
          - { os: windows-latest, rid: win-x64   }
          - { os: macos-latest,   rid: osx-arm64 }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 1 }        # 冒烟不需要 submodule/testdata
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - run: dotnet publish src/Ts.Cli/Ts.Cli.csproj -c Release -r ${{ matrix.rid }}
      - name: run --version and assert
        shell: bash
        run: |
          bin=$(find src/Ts.Cli/bin/Release -type d -name publish | head -1)
          out=$("$bin/tscs" --version)
          test "$out" = "Version 7.1.0-dev"
```

要点：① analyzer 门禁不设独立 job——`TreatWarningsAsErrors` 使 build 即门禁；② linux AOT 依赖 clang/zlib，GitHub `ubuntu-latest` 预装（若换自托管 runner 须补 `apt-get install clang zlib1g-dev`）；③ 阶段 5 起 aot-smoke 扩为「AOT 二进制复跑一致性套件」双轨（01 §8.4）；④ `tscs --version` 断言字符串与 MSBuild `TsVersion`/`TsVersion.Value` 三点一致性可加一步 grep 校验。

### 1.14 阶段 0 验收 checklist

主计划 §二 阶段 0 门禁行展开为可勾选项（全勾 + 11-progress.md 签核行 = 阶段 0 出口）：

**门禁项（主计划原文）**
- [ ] 三平台（linux-x64 / win-x64 / osx-arm64）`dotnet publish /p:PublishAot=true` 产出 tscs，`tscs --version` 输出 `Version 7.1.0-dev`（CI aot-smoke 三绿）
- [ ] `Ast/*.g.cs` ×7 生成、入库，AOT+Trim+SingleFile analyzer 零告警（TreatWarningsAsErrors 下 build 绿）（R2：主计划「~5 个」为约数，定稿 7 文件，02 §16）
- [ ] `Diagnostics/Messages.g.cs` 计数 == 2,154（xUnit 断言过）

**脚手架完备项（主计划阶段 0 范围列举 + 00-overview.md §6.1）**
- [ ] git init 完成、首提干净（无 obj/bin/node_modules 入库）
- [ ] `.gitignore`、`.editorconfig`、`BannedSymbols.txt` 落库
- [ ] Directory.Build.props ×3 与 07-aot-constraints.md §3 全文一致（§1.4 差异表清零）
- [ ] Directory.Packages.props 含 §1.5 全部包（BannedApiAnalyzers 已补）
- [ ] `_submodules/TypeScript` 钉扎到坐标 B；UPSTREAM.lock 落库（**被待澄清 1 阻塞**）
- [ ] `tools/check-upstream-pin.sh` 五项校验本地过、接入 CI 门 1
- [ ] `tools/sync-testdata.sh` 跑通，baselines/reference 49,321 个 Git 跟踪文件入库、镜像总计数与逻辑字节数入 lock（R14）
- [ ] `tools/dumptool/patches/` 骨架 + `regen.sh` + `fixtures.lock.json` 初版落库（W0-22，R16）
- [ ] 夹具存储通道已定案（Git LFS / 独立 fixtures 仓库 / CI artifact 三选一，08-testing-strategy.md §11 待澄清第 1 条；R16 列为阶段 0 出口项）
- [ ] `Bundled/libs` 108 个 EmbeddedResource（xUnit 资源计数断言过）
- [ ] Tracing 接口占位编译过
- [ ] LargeStackExecutor 单测绿（重入/异常传播/深递归金丝雀初版）
- [ ] CI 三 job（pin-and-drift / build-test ×3 / aot-smoke ×3）全绿
- [ ] plan/ 00–12 十三份文档齐全
- [ ] 11-progress.md 记录阶段 0 签核行

---

## 2. 阶段 1 WBS——地基（core / collections / tspath / nativepath / vfs / jsnum / stringutil / glob / semver）

### 2.1 范围、门禁与总量

主计划 §二 阶段 1 行：core 2.7k、collections 0.8k、tspath 1.4k、vfs 3.1k、jsnum 0.6k、stringutil 5.6k、glob、semver（LOC 均为去测试实测；01 §2.3 另把 **nativepath 298 行**列入 L0 阶段 1——osvfs 的 Realpath 依赖它，本文纳入）。**出口门禁**：① 移植的 Go 单测全绿；② jsnum 门禁——数字→字符串 / ToInt32 / ToUint32 / Number 解析对 100 万随机 double 夹具逐位相等（细则 §2.4）。

Go 平台垫片按 01 §2.3 口径处理：`debug`（61 行断言助手）→ `Ts.Core.Debug` 静态类，阶段 1 顺手落地（core.go:584 就用到 `debug.Assert`）；`json` 包不移植，三个阶段 1 触点的替代法在映射表内逐行注明。

### 2.2 移植次序（依赖序）

对全部阶段 1 包的 import 实测（已剔除 `_test.go` 边）得到承重边：

| 依赖边 | 证据 |
|---|---|
| core → stringutil, tspath, collections, json, debug | core/core.go import 块；core/bfs.go → collections（OrderedMap 做确定性层级） |
| tspath → stringutil | tspath/path.go import 块 |
| jsnum → stringutil, json | jsnum/string.go:83,288-323（IsDigit/IsOctalDigit/IsHexDigit）；string.go:37（json.Marshal 浮点格式） |
| collections → json | collections/ordered_map.go:217,263（MarshalJSONTo/UnmarshalJSONFrom） |
| vfs/osvfs → core, nativepath | vfs/osvfs/os.go:14-19（LimitedSemaphore 节流、Realpath） |
| vfs/* → tspath, vfs | vfs/internal/internal.go、iovfs、vfsmatch 等 |

据此的**建议次序**（在任务书建议序「core.text→tristate→core 助手→collections→tspath→vfs 分层→jsnum→stringutil」基础上按实测依赖做两处修正：stringutil 必须先于 tspath/core 助手/jsnum；collections 先于 core 助手——理由即上表）：

| 波次 | 内容 | 可并行性 |
|---|---|---|
| 1 | core 零依赖小件：text.go、tristate.go、stack.go、binarysearch.go、pattern.go、version.go 补全 | 与波 2 并行 |
| 2 | stringutil 全包（叶包；含两张生成表转写） | 与波 1 并行 |
| 3 | collections 全包（叶包，ordered_map 的 JSON 面推迟） | glob、semver（零依赖叶包）可在任意波次穿插 |
| 4 | tspath（依赖 stringutil） | |
| 5 | core 其余：Debug 助手 → core.go 泛型助手群 → semaphore/bfs/linkstore/context/nodemodules → compileroptions 族 → **workgroup.go（+LargeStackExecutor 池化演进，01 §4.5/4.6）** | |
| 6 | nativepath → vfs 分层：vfs.go 接口 → internal → iovfs → osvfs → vfstest → cachedvfs / trackingvfs / wrapvfs → vfsmatch（vfsmock 见待澄清 5） | |
| 7 | jsnum（依赖 stringutil；含门禁夹具） | |

### 2.3 文件映射表（Go 文件 → C# 文件 + 测试文件 + 注意点）

命名总则：Go 文件 → PascalCase 同名 .cs（主计划 §一.3）；测试落 `tests/Ts.Compiler.Tests/<包名>/<文件>Tests.cs`。「✂️」= 不移植（理由必填）。

#### 2.3.1 internal/core → src/Ts.Compiler/Core/（Ts.Core）

| Go 文件 | LOC | C# 文件 | 测试 | 注意点（一句话） |
|---|---:|---|---|---|
| arena.go | 66 | ✂️ | — | 删除全部 Arena（主计划 §一.1）；`arena.New()`→`new T()` 全局约定 |
| bfs.go | 206 | Bfs.cs | BfsTests.cs（bfs_test.go 152） | 并行 BFS；层级刻意用 OrderedMap 保确定性，原样直译；atomic→Interlocked |
| binarysearch.go | 26 | BinarySearch.cs | — | 复刻 Go sort.Search「返回插入点」语义，勿用 Array.BinarySearch（找不到时返回补码，语义不同） |
| buildoptions.go | 16 | BuildOptions.cs | — | 小 options struct |
| compileroptions.go | 557 | CompilerOptions.cs | — | ~150 字段 + ModuleKind/ScriptTarget/ModuleResolutionKind 等枚举本体；字段大量 Tristate；反射 merge/遍历不在此（阶段 4 生成 per-field switch） |
| context.go | 42 | Context.cs | — | context.Value 键值对 → RequestContext 显式字段（01 §4.4）；CheckerLifetime 枚举照搬 |
| core.go | 837 | Core.cs | CoreTests.cs | ~70 个泛型切片/映射助手（Filter/Map/Same*/Memoize/…）逐函数直译；`UTF16Len(s)` 在 C# 即 `s.Length`，ComputeECMALineStarts 按 UTF-16 code unit（01 §6）；levenshtein 按 rune → 按码点迭代；StringifyJson（core.go:522-523 json.MarshalIndent）→ 手写 Utf8JsonWriter（07 §4.2 面 1）；ApplyDebugStackLimit（core.go:24-34，rtdebug.SetMaxStack）✂️ 由 LargeStackExecutor 取代 |
| languagevariant.go | 11 | LanguageVariant.cs | — | 枚举 |
| languagevariant_stringer_generated.go | 25 | （家族 6） | — | 仅基线承重的枚举才生成 ToDisplayString（主计划 §一.4）；接入时机归 04-codegen.md |
| linkstore.go | 30 | LinkStore.cs | — | `map[K]*V + Arena` → `Dictionary<K,V>`（V: class, new()）包装；K 为引用类型（Go 指针键）时必须配 ReferenceEqualityComparer（主计划 §一.6） |
| modulekind_stringer_generated.go | 52 | （家族 6） | — | ModuleKind 枚举本体在 compileroptions.go |
| nodemodules.go | 90 | NodeModules.cs | — | `map[string]bool` 常量集 → `FrozenSet<string>`（StringComparer.Ordinal） |
| parsedoptions.go | 10 | ParsedOptions.cs | — | — |
| pattern.go | 52 | Pattern.cs | PatternTests.cs（24） | paths 通配 Pattern{Text,StarIndex}；strings.Index→IndexOf(…, StringComparison.Ordinal) |
| projectreference.go | 20 | ProjectReference.cs | — | — |
| scriptkind.go | 21 | ScriptKind.cs | — | 枚举 |
| scriptkind_stringer_generated.go | 31 | （家族 6） | — | — |
| scripttarget_stringer_generated.go | 50 | （家族 6） | — | ScriptTarget 枚举本体在 compileroptions.go |
| semaphore.go | 52 | Semaphore.cs | — | Unlimited/LimitedSemaphore（chan struct{}）→ SemaphoreSlim 包装；TryAcquire(ctx) → CancellationToken 重载 |
| stack.go | 33 | Stack.cs | — | 泛型栈；空 Pop panic → InvalidOperationException；`data[l-1]=zero` 清引用照搬（GC 友好） |
| text.go | 82 | Text.cs | — | TextPos(int32)/TextRange readonly struct；UndefinedTextRange=(-1,-1)；这是全仓位置类型的根，字段宽度勿改 |
| textchange.go | 30 | TextChange.cs | — | — |
| tristate.go | 71 | Tristate.cs | — | byte 枚举，**TSUnknown=0 的零值语义承重**（omitzero/默认值判断），基值顺序勿动 |
| tristate_stringer_generated.go | 26 | （家族 6） | — | — |
| typeacquisition.go | 24 | TypeAcquisition.cs | — | — |
| version.go | 33 | Version.cs ⚠️ | — | 已有 `TsVersion.Value`；补 `VersionMajorMinor`（version.go:14-33；无效版本 panic → 静态构造抛异常） |
| watchoptions.go | 53 | WatchOptions.cs | — | — |
| workgroup.go | 123 | WorkGroup.cs | WorkGroupTests.cs | 01 §4.5 接口设计权威：单线程实现**尾部 LIFO 弹出**且支持执行中再入队（fileloader 递归发现承重，workgroup.go:78-89）；done 后 Queue → InvalidOperationException；ParallelWorkGroup 注入 LargeStackExecutor；同一 PR 内完成执行器池化演进（§1.10） |
| （debug 包 debug.go） | 61 | Debug.cs | DebugTests.cs | `Ts.Core.Debug.Assert`（01 §2.3 吸收口径）；Release 下行为与 Go 一致（Go 的 debug.Assert 无编译开关，勿用 [Conditional("DEBUG")] 走样） |

#### 2.3.2 internal/collections → src/Ts.Compiler/Collections/（Ts.Collections）

| Go 文件 | LOC | C# 文件 | 测试 | 注意点 |
|---|---:|---|---|---|
| cow.go | 76 | Cow.cs | — | CopyOnWriteMap/Set；`EnterScope() func()` 恢复闭包 → IDisposable/using（主计划 §一.6） |
| multimap.go | 75 | MultiMap.cs | — | map K→[]V；切片增删语义照搬 |
| ordered_map.go | 316 | OrderedMap.cs | OrderedMapTests.cs（187） | 插入序 map：显式 key 列表 + 字典，**勿依赖 .NET Dictionary 的巧合插入序**（主计划 §一.6 审计规则的正面教材）；MarshalJSONTo/UnmarshalJSONFrom（:217,263）不做通用 JSON——消费点（showconfig/buildinfo）以手写 writer 接管（07 §4） |
| ordered_set.go | 54 | OrderedSet.cs | OrderedSetTests.cs（53） | OrderedMap 包装 |
| set.go | 144 | Set.cs | — | map[T]struct{} 包装（Has/Add/Delete/Clone…）；Go 迭代随机 ⇒ 每个迭代消费点按审计规则标注（05-conventions.md） |
| syncmap.go | 98 | SyncMap.cs | SyncMapTests.cs（32） | sync.Map → ConcurrentDictionary 包装；LoadOrStore → GetOrAdd（返回既有值语义一致）；Size/ToMap 助手照搬 |
| syncset.go | 77 | SyncSet.cs | — | ConcurrentDictionary<T,byte> 承载 |

#### 2.3.3 internal/tspath → src/Ts.Compiler/TsPath/（Ts.TsPath）

| Go 文件 | LOC | C# 文件 | 测试 | 注意点 |
|---|---:|---|---|---|
| path.go | 1219 | Path.cs | PathTests.cs（path_test.go 818）、StartsWithDirectoryTests.cs（177）、UntitledTests.cs（61） | 65 个路径代数函数（GetEncodedRootLength:168 / NormalizePath:599 / ToPath:687 …）全部 1:1；`type Path string` 强类型键 → 包装（建议 readonly record struct，最终由 05-conventions.md 定）；大小写折叠只许走 stringutil，禁 culture API；`^/untitled/` 特例有专测 |
| extension.go | 198 | Extension.cs | — | 扩展名常量表与判定；比较一律 Ordinal |
| ignoredpaths.go | 18 | IgnoredPaths.cs | IgnoredPathsTests.cs（133） | node_modules/.git 等忽略前缀判定 |

#### 2.3.4 internal/nativepath → src/Ts.Compiler/NativePath/（Ts.NativePath）

Go 用构建标签分平台文件；C# 无构建标签，组织为**单入口 + `OperatingSystem.IsXxx()` 分支**（或平台后缀部分类），P/Invoke 一律 `[LibraryImport]`（源生成，AOT 安全，07 §2）。

| Go 文件 | C# 归置 | 注意点 |
|---|---|---|
| realpath_darwin.go | Realpath.cs（darwin 分支） | open(O_EVTONLY)+fcntl(F_GETPATH) 的 O(1) 快路径（realpath_darwin.go:11-49）；P/Invoke 直译或先退化到通用分支——行为以 osvfs/realpath_test.go 移植件钉死 |
| realpath_linux.go | Realpath.cs（linux 分支） | /proc/self/fd readlink 技法（realpath_linux.go:39） |
| realpath_windows.go | Realpath.cs（windows 分支） | 句柄取规范路径（GetFinalPathNameByHandle 等价）；**兼纠正文件名大小写**——vfs.Realpath 的文档义务（vfs.go:47-49） |
| realpath_other.go | Realpath.cs（默认分支） | filepath.EvalSymlinks 等价：逐段解析 symlink（File.ResolveLinkTarget 循环） |
| symlink_windows.go(+test) / symlink_other.go | Symlink.cs | reparse point 判定（osvfs 枚举目录时用） |
| eintr_unix.go | ✂️（按需内联） | .NET 运行时对系统调用自带 EINTR 重试；仅自写 P/Invoke 处需要 retry 循环 |

#### 2.3.5 internal/vfs（含 8 子包 + internal）→ src/Ts.Compiler/Vfs/（Ts.Vfs.*）

**结构性注意点（先读）**：Go 的 vfs 建立在标准库 `io/fs` 抽象上（`fs.FS`/`fs.Stat`/`fs.ReadDir`/`fstest.MapFS`），.NET 没有等价物。移植时在 `Ts.Vfs.Internal` 自建最小文件系统 SPI（Open/Stat/ReadDir/ReadFile，语义对齐 io/fs），osvfs 用 BCL 文件 API 实现、vfstest 用内存 map 实现——**接口形状以 vfs.go 的 FS 十一方法为唯一公共面**（vfs.go:12-50），SPI 只在 Internal 命名空间内部。`fs.SkipDir`/`fs.SkipAll` 哨兵错误 → WalkDir 回调返回值枚举（05-conventions.md error→返回值逐案表）。

| Go 文件 | LOC | C# 文件 | 测试 | 注意点 |
|---|---:|---|---|---|
| vfs.go | 88 | Vfs/Vfs.cs | VfsTests.cs（65） | FS 接口 + Entries{Files,Directories,Symlinks} + FileInfo/DirEntry 等价物；接口命名（FS vs IFileSystem）由 05-conventions.md 终裁 |
| internal/internal.go | 194 | Vfs/Internal/Internal.cs | InternalTests.cs（新增夹具） | **BOM/UTF-16 解码必须逐字节复刻**（internal.go:169-194）：FFFE→UTF16LE、FEFF→UTF16BE、EFBBBF 剥除；`len/2` 整除截断——奇数尾字节**丢弃**，而 .NET Encoding.Unicode 会产出 U+FFFD，**不能用 Encoding 直接解码，须手写循环**；无孤立代理正规化差异（utf16.Decode 无效对→U+FFFD，与 .NET 一致）；binary.Read 失败→空串分支照搬 |
| iovfs/iofs.go | 222 | Vfs/IoVfs/IoFs.cs | IoFsTests.cs（134） | fs.FS→FS 适配层（含可写扩展）；bundled 只读 FS 与 vfstest 的挂载点 |
| osvfs/os.go | 242 | Vfs/OsVfs/Os.cs | OsTests.cs（67）+HelpersTests.cs（27）+RealpathTests.cs（152） | 真 OS 实现：启动期大小写敏感性探测用**自身可执行文件路径变换大小写再 stat**（os.go:45-70）→ Environment.ProcessPath 复刻；读/写/阻塞三类 LimitedSemaphore 节流 128/128/32（os.go:21-27）照搬；reparse point 走 nativepath；symlink 测试在三平台 CI 分别跑 |
| cachedvfs/cachedvfs.go | 154 | Vfs/CachedVfs/CachedVfs.cs | CachedVfsTests.cs（350） | 装饰器缓存 stat/readdir/realpath（SyncMap 承载）；ClearCache 语义照搬 |
| trackingvfs/trackingvfs.go | 76 | Vfs/TrackingVfs/TrackingVfs.cs | — | 记录读取集的装饰器 |
| vfsmatch/vfsmatch.go | 717 | Vfs/VfsMatch/VfsMatch.cs | VfsMatchTests.cs（1,938） | include/exclude/wildcard 匹配的**非正则重写算法**（等价性论证见同目录 MATCHING_ALGORITHM.md）——逐行直译，禁止"顺手优化"；测试量最大，全部搬 Theory |
| vfsmatch/stringer_generated.go | 26 | （家族 6） | — | — |
| vfstest/vfstest.go | 679 | Vfs/VfsTest/VfsTest.cs | VfsTestTests.cs（751） | 内存 FS（fstest.MapFS 等价物自建）：大小写敏感/不敏感双模式、symlink 模拟；生产不引用但**阶段 1 起所有 vfs 相关测试都要它，优先移植** |
| vfsmock/mock_generated.go(+wrapper.go) | 601 | ✂️（暂定） | — | moq 工具生成的 mock——不移植生成物，以手写委托字段 fake 替代；终裁归 08-testing-strategy.md（待澄清 5） |
| wrapvfs/wrapvfs.go | 132 | Vfs/WrapVfs/WrapVfs.cs | — | 方法覆盖式包装器 |

#### 2.3.6 internal/jsnum → src/Ts.Compiler/JsNum/（Ts.JsNum）

| Go 文件 | LOC | C# 文件 | 测试 | 注意点 |
|---|---:|---|---|---|
| jsnum.go | 177 | JsNum.cs | JsNumTests.cs（740） | `type Number float64` → readonly struct Number(double)；toInt32/toUint32 的 modulo-2³² 语义（jsnum.go:45-72）逐行直译；Go `math.Mod` == C# 的 double `%`（截断余数），**不是** Math.IEEERemainder；Remainder/Exponentiate（:130,147）的 ES 特例分支照搬 |
| string.go | 341 | String.cs | StringTests.cs（366） | Number→String：NaN/±Infinity、安全整数快路径（string.go:29-34）之外**委托 json.Marshal 取最短往返**（string.go:36-38）→ C# 用 double.ToString 默认最短往返 + ES6 版式层（§2.4）；FromString（:41+）先修补字符串再交 strconv → C# 复刻同一修补 + InvariantCulture 解析；超 64 位 hex/octal/binary 走 math/big → BigInteger；依赖 stringutil.IsDigit 等（:83,288-323）⇒ stringutil 先行 |
| pseudobigint.go | 66 | PseudoBigInt.cs | PseudoBigIntTests.cs（77） | bigint 字面量 {negative,base10Value} 规范化；纯字符串算法 |
| ryu_test.go | 164 | RyuTests.cs | （即测试） | 无对应源文件——纯格式向量表，转 xUnit Theory 钉死格式层 |

#### 2.3.7 internal/stringutil → src/Ts.Compiler/StringUtil/（Ts.StringUtil）

| Go 文件 | LOC | C# 文件 | 测试 | 注意点 |
|---|---:|---|---|---|
| compare.go | 128 | Compare.cs | — | 大小写不敏感比较是**自研实现**（compare.go:34-63），逐行直译；禁 string.Compare(culture)/CompareOptions（07 §7.3） |
| identifier.go | 17 | Identifier.cs | — | 二分查 Unicode 标识符区间表 |
| identifier_parts_generated.go | 1,391 | IdentifierParts.g.cs | — | 区间表一次性转写 `static ReadOnlySpan<T>`（RVA 静态数据零分配，主计划家族 5） |
| js_case.go | 157 | JsCase.cs | JsCaseTests.cs（49） | 复刻 JS 无 locale 的 toLowerCase/toUpperCase：ASCII 快路径、final sigma 上下文规则（js_case.go:135-153）；**禁止**以 char.ToLowerInvariant 直接替代——必须走移植表 |
| js_case_generated.go | 3,496 | JsCaseTables.g.cs | — | 特殊大小写映射表转写（同上 RVA） |
| util.go | 380 | Util.cs | UtilTests.cs（59） | EncodeURI 逐字节转义表（:153-189）、SplitLines、BOM 助手、surrogate 对助手（:289-361）；rune 遍历 → 码点遍历（05-conventions.md UTF-16 节） |
| generate.go | 4 | ✂️ | — | go:generate 指令壳 |
| _scripts/generate-unicode-data.mts | — | tools/generator/ 收编 | — | 家族 5 的上游再生成脚本；升级 pin 时按 04-codegen.md 流程重跑 |

#### 2.3.8 internal/glob → src/Ts.Compiler/Glob/（Ts.Glob）

| Go 文件 | LOC | C# 文件 | 测试 | 注意点 |
|---|---:|---|---|---|
| glob.go | 349 | Glob.cs | — | LSP 3.17 glob（上游自注「目前仅测试用途」，glob.go:17-23——原样保留该注释与语义）；utf8.DecodeRuneInString → 码点迭代；element 接口 → 密封类层次 |

#### 2.3.9 internal/semver → src/Ts.Compiler/Semver/（Ts.Semver）

| Go 文件 | LOC | C# 文件 | 测试 | 注意点 |
|---|---:|---|---|---|
| version.go | 274 | Version.cs | VersionTests.cs（147） | 三个正则（version.go:19,27-29）→ `[GeneratedRegex]`（源生成，AOT 安全）；prerelease 全序比较规则逐行直译 |
| version_range.go | 438 | VersionRange.cs | VersionRangeTests.cs（959） | `^ ~ - x` range 语法；巨型测试表直接搬 Theory |

### 2.4 jsnum 门禁落地（阶段 1 出口的硬项）

主计划已核验：.NET double.ToString 自 Core 3.0 即最短往返，**只需在其上加 ES6 版式层**。版式层规则（全部来自主计划阶段 1 行，测试向量出自 jsnum/string_test.go）：

1. 安全整数快路径：`MinSafeInteger ≤ n ≤ MaxSafeInteger` 且整数 → 十进制整数串（string.go:29-34 同构）；
2. `|n| ≥ 1e21` 与 `0 < |n| < 1e-6` 切指数记法；`e+`/`e-` 后**不补零**（`1e+21` 非 `1e+021`）；
3. `-0` → `"0"`；NaN → `"NaN"`；±∞ → `"Infinity"`/`"-Infinity"`；
4. 钉死向量：`{1e21,"1e+21"}`、`{1e20,"100000000000000000000"}` 等 string_test.go 全表。

**夹具**：Go 侧一次性生成器（挂 dumptool 补丁分支，规格已入 08-testing-strategy.md §2）导出 100 万随机 double + 边界集（±0、次正规、2⁵³±1、1e21/1e-6 邻域、DBL_MAX/MIN），每行记 `bits(hex u64), String(), ToInt32, ToUint32, FromString(String()) 位相等标志`；C# 侧 xUnit 读夹具**逐位断言**四向。夹具文件入仓库根 `fixtures/jsnum/`（R14：自有夹具不进 testdata/，testdata/ 保持 tsgo 纯镜像；随 `fixtures.lock.json` 管理）。

### 2.5 横切纪律（阶段 1 起每个 PR 自查）

- **map 迭代序审计**：每个 `for range m` 移植点标注「序无关」或补 Go 同款排序（主计划 §一.6 审计规则；OrderedMap/Set 的存在本身就是上游对序敏感点的显式标记）。
- **nil→null 逐点直译**：NRT 全开下由编译器审计（07 §3.6 Nullable 行）。
- **一切字符串比较 Ordinal**；大小写路径只走 stringutil（07 §7.2 合法路径全集）。
- **UTF-16**：rune 循环 → 码点迭代助手；`len(string)`（字节长）出现处逐个判别语义（05-conventions.md）。

### 2.6 阶段 1 验收 checklist

- [ ] §2.3 九张映射表全部行完成（✂️ 行在 06-module-inventory.md 状态列记「不移植+理由」）
- [ ] 移植的 Go 单测全绿：core（bfs/pattern）、collections（ordered_map/ordered_set/syncmap）、tspath（path/ignoredpaths/startsWithDirectory/untitled）、vfs（vfs/iovfs/os/realpath/helpers/cachedvfs/vfsmatch/vfstest）、jsnum（jsnum/string/pseudobigint/ryu）、stringutil（js_case/util）、semver（version/version_range）
- [ ] jsnum 门禁：100 万 double 夹具四向逐位相等（§2.4）
- [ ] BOM/UTF-16 解码新增夹具过（奇数尾字节、无效代理对、双 BOM 变体）
- [ ] WorkGroup 双实现语义测试过（LIFO、执行中再入队、done 守卫）；LargeStackExecutor 完成池化演进且金丝雀仍绿
- [ ] Windows CI 上 tspath/vfs 全绿（路径/大小写行为，12-risks.md 风险项）
- [ ] AOT/Trim analyzer 保持零告警；aot-smoke 仍绿（新增代码未引入 IL 告警）
- [ ] 每个 `for range` 移植点已标注序无关/已排序（PR 模板勾选项）
- [ ] 11-progress.md 更新 9 个包的百分比与签核行

---

## 3. 后续阶段梗概（滚动波之外，只此一段）

阶段 2（Scanner，UTF-16 偏移、全语料 token 流 diff 100%）→ 阶段 3（AST+Parser+testrunner，AST 转储与解析诊断三元组 100%）→ 阶段 4（Binder/tsoptions/模块解析，symbol/flow 转储 100%）→ 阶段 5（Checker 主峰，errors.txt 25%→100% 分级爬坡，再 .types/.symbols）→ 阶段 6（Emit，js/d.ts/map 字节匹配）→ 阶段 7（tsc CLI+增量+AOT 性能门禁）→ 阶段 8（语言服务/fourslash）→ 阶段 9（API/长尾）→ 阶段 10（加固）。各阶段进入/退出条件、重叠 DAG 与分级子门禁的权威展开在 **09-phases.md**；阶段 1 门禁签核后，本文改版为「阶段 1 收尾 + 阶段 2 文件级 WBS」。

---

## 4. 待澄清（不改设计，列给主计划裁决）

1. ~~**坐标 A 未知（阻塞 W0-03/04）**~~——**已关闭（2026-07-11）**：排除 5 个 Finder `.DS_Store` 后，本地 55,019 个文件重建的根树精确等于 `23c5ee2aa33485d7d55e38edc60eb740a47ac562`，故坐标 A=`168e7015edf98244febc8f4ae450b673b5d195d7`；其 gitlink 给出坐标 B=`4d4f005c8541e0255a9d8791205fdce326e462bc`。保持现有 zip 蓝本只读，在目标仓直接钉子模块。
2. **阶段 0 生成器自举需要最小手写 AST 支撑面**（Node 基类五字段、NodeList、NodeFlags 等枚举壳）才能让 Ast/*.g.cs 过编译与 analyzer；主计划把 ast 手写半区划在阶段 3。本文按「最小占位股」处理（§1.11），请 02-ast-design.md 确认占位面清单与形状。
3. **LargeStackExecutor 两形态**：仓库现状是 static 简版（thread-per-call），01-architecture.md §4.6 是池化实例设计（Default/Schedule/RunAsync/帮工循环）。本文把演进排在阶段 1 workgroup.go 移植同一 PR；若架构要求阶段 0 即池化，请裁决。
4. **System.Threading.Channels**：主计划 §一.3 将其列入 Directory.Packages.props 示例，但 net10.0 下它是框架内置程序集，无需包版本行。本文按内置处理（§1.5），请主计划下次修订时更正示例。
5. **vfsmock（moq 生成 601 行）**：本文暂定不移植生成物、以手写 fake 替代，终裁归 08-testing-strategy.md。
6. **根 Directory.Build.props 的 `TsVersion` 属性**：仓库现状有、07-aot-constraints.md §3.2 全文没有。本文选择保留并提请 07 修订并入（版本字符串三点一致性校验依赖它）。
7. **`.g.cs` 与 analyzer 的交互**：门禁要求 Ast/*.g.cs 过 AOT+Trim analyzer，而部分 CA 规则默认跳过 `generated_code=true` 文件。本文的 .editorconfig 只豁免 IDE 风格诊断（§1.6-4）；若实测发现 IL 族诊断也被 generated_code 抑制，需在 07-aot-constraints.md 增补对策（如对 .g.cs 关闭 generated_code 标记、改用文件头注释豁免风格）。
8. ~~**testdata 指纹计数与字节单位冲突**~~——**已关闭（2026-07-11）**：坐标 A 的 Git 树证明 reference=49,321、testdata 总文件=49,642；本地旧计数多出的 2/4 个文件均为 `.DS_Store`。原模板 `du -sb` 在 macOS 不存在，原脚本 `du -sk` 又是 KiB 块占用。§1.2/§1.12 已统一为排除 `.DS_Store`，并以逐普通文件 `wc -c` 求和定义跨平台稳定的逻辑字节指纹。
