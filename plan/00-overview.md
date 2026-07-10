# 00 — 项目总览：目标、范围、非目标、上游钉扎与阅读指南

| 元信息 | |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-07-11 |
| 状态 | Draft |
| 主计划 | `~/.claude/plans/precious-riding-hoare.md`（已批准；本文档是其 §三 文档集中的 00 号） |
| 上游快照 | `/Users/token/Desktop/code/typescript-go-main`（tsgo，`core.Version()` = `"7.1.0-dev"`，internal/core/version.go:8） |
| 目标仓库 | `/Users/token/Desktop/code/typescript-csharp`（根命名空间 `Ts`） |

---

## 1. 项目目标

把微软官方 Go 版 TypeScript 编译器 **tsgo**（typescript-go，生产代码 300,749 行）**全量复刻**为 C#/.NET 10 **Native AOT** 实现。所有规则、算法、架构设计 1:1 复用 tsgo；不做行为改进，不做行为删减（明确列入 §3 非目标者除外）。

三条已确认的顶层决策（来自主计划 Context，不得在任何子文档中被推翻）：

1. **范围**：全量复刻，编译器优先——里程碑 1 = tsc CLI；里程碑 2 = LSP / 语言服务 / 项目系统 / API。
2. **风格**：结构 1:1（Go 包 → C# 命名空间、Go 文件 → C# 文件、逐函数直译）+ C# 惯例命名。惯用法映射细则见 05-conventions.md。
3. **验证**：复用官方 conformance 基线——移植 testrunner，对 `testdata/baselines/reference/` 下 49,323 个基线文件做**逐字节 diff**。「与 tsgo 输出逐字节相等」是本项目唯一的验收货币。

一句话判据：**任何一个移植决策，若无法回答“它在哪个门禁里被逐字节验证”，就还不算完成设计。**

### 1.1 成功判据（可执行版本）

- 里程碑 1 完成 = 主计划 §二 阶段 0–7 全部出口门禁签核（终点：三平台 AOT 二进制编译真实大项目，输出与 tsgo 逐字节全等，性能 ≤ 2× tsgo）。
- 里程碑 2 完成 = 阶段 8–10 全部出口门禁签核（终点：fourslash 99%+，官方 VS Code 扩展 `_extension` **不经修改**即可驱动我们的二进制）。「不经修改即可用」的测量方式见 §2.3 末段（阶段 10 扩展冒烟门禁，裁决 R19）。
- 全程约束：`IsAotCompatible` + AOT/Trim analyzer 从第 0 天起全开（见 07-aot-constraints.md）；单线程与 4-checker 并行两种模式输出全等（确定性检验，主计划 §四.3）。

---

## 2. 范围与里程碑边界

### 2.1 上游 CLI 表面 = 边界的物理定义

tsgo 单一二进制以首个参数三路分派（cmd/tsgo/main.go:20-31）：

```go
switch args[0] {
case "--lsp":  return runLSP(args[1:])   // cmd/tsgo/lsp.go
case "--api":  return runAPI(args[1:])   // cmd/tsgo/api.go
}
// 默认：tsc 等价 CLI
result := execute.CommandLine(ctx, newSystem(), args, nil)
```

`Ts.Cli` 复刻同一分派结构（主计划 §一.3：“默认 tsc / --lsp / --api 三路复用”）。**里程碑边界即这三路的实现完成度**：

| 路 | 里程碑 1 | 里程碑 2 |
|---|---|---|
| 默认（tsc） | ✅ 全功能 | — |
| `--lsp` | 占位（保留分派、返回未实现错误码） | ✅ 全功能 |
| `--api` | 占位（同上） | ✅ 全功能 |

### 2.2 里程碑 1：tsc CLI（阶段 0–7，占比约 78%）

**功能面**（以 `internal/execute` 的 CommandLine 全部可达路径为准）：

- 单次编译：命令行/`tsconfig.json` 解析（tsoptions 全部 ~150 选项含 `--showConfig`）、模块解析（含 trace）、类型检查全量诊断、JS/d.ts/sourcemap 发射。
- `--watch`：watch 编排循环（internal/execute/watcher.go、internal/execute/watchmanager/）。
- `--build`/`-b`：项目引用 DAG 构建模式（internal/execute/build/orchestrator.go）。
- 增量**构建**：`.tsbuildinfo` 读写，与 tsgo **字节相等**（internal/execute/incremental/snapshot.go、buildInfo.go；字节保真规格见主计划 §一.5 与 01-architecture.md）。注意与「增量解析」区分——后者是非目标，见 §3.1。
- 诊断输出：纯文本与 `--pretty` 两种，**仅英文**（locale 包归阶段 9，见 §2.4 裁定 6）。

**包含的 Go 包**（预期归属；精确清单、LOC 与危险点以 06-module-inventory.md 为准）：

| 层 | 包 |
|---|---|
| 地基（阶段 1） | core、collections、tspath、vfs、jsnum、stringutil、glob、semver、json、debug、nativepath |
| 前端（阶段 2–3） | scanner、ast（半生成）、astnav、parser |
| 中端（阶段 4） | binder、tsoptions、module、packagejson、symlinks、compiler（program/fileloader/checkerpool）骨架 |
| 主峰（阶段 5） | checker、evaluator、nodebuilder、pseudochecker、modulespecifiers（d.ts 发射合成 import specifier 需要） |
| 后端（阶段 6） | printer、transformers、sourcemap、outputpaths |
| CLI（阶段 7） | execute（含 build/incremental/watchmanager/tsc）、diagnostics、diagnosticwriter、bundled（108 个 lib.*.d.ts 内嵌资源，internal/bundled/libs/） |
| 测试基架（阶段 3 起） | testrunner、testutil、repo（路径定位助手，internal/repo/paths.go） |

**里程碑 1 出口门禁**（= 主计划 §二 阶段 7 行）：Go execute 基线测试过；`.tsbuildinfo` 与 tsgo 字节相等；增量输出 = 全量输出；发布的 AOT 二进制编译真实大项目输出与 tsgo 全等，性能 ≤ 2× tsgo。在此之前，阶段 5/6 已要求 conformance 全部 `.errors.txt`/`.types`/`.symbols`/`.js`/`.d.ts`/`.js.map` 100% 字节匹配。

### 2.3 里程碑 2：LSP / 语言服务 / 项目系统 / API（阶段 8–10，占比约 22%）

**功能面**：

- `--lsp`：stdio 上的 JSON-RPC 语言服务器（internal/lsp 21k、internal/ls 38.9k、internal/format 4.3k、internal/project 11k、internal/jsonrpc），协议面 = LSP 元模型生成的 Protocol.g.cs + tsgo 自定义扩展。
- `--api`：手写 msgpack 帧的进程外 API（internal/api 9.1k，阶段 9）。
- fswatch：**基于 FileSystemWatcher 重写**（非直译，见 §3.2），供 LSP 侧（internal/lsp/lspwatcher/）与 CLI watch 真实后端共用。
- 长尾：tracing → EventSource、locale（本地化诊断）。

**兼容性目标（承重）**：C# 二进制在 LSP 与 API 两条线缆协议上与 tsgo **协议兼容**，从而官方 VS Code 扩展 `_extension`（以 `tsgo --lsp` + stdio 启动服务器，_extension/src/client.ts:178-191）与 npm 包 `@typescript/native-preview`（_packages/native-preview/，bin 包装二进制）**无需重写即可复用**——只需替换其定位的可执行文件（_extension/src/util.ts:46-54 按名字查找 `tsgo`/`tsc` 二进制）。这就是 §3.3 「不重写扩展」非目标的成立前提。该目标的可执行测量（裁决 R19）：阶段 10 门禁新增一项**扩展冒烟**——按 `_extension` 真实启动路径 spawn 我们的二进制 `--lsp` + stdio，跑核心交互脚本；方法写入 08-testing-strategy.md，门禁编号归 09-phases.md（补写中，R20）。

**里程碑 2 出口门禁**（= 主计划 §二 阶段 8–10 行）：fourslash 分级 50%→90%→99%+；LSP stdio 回放集成测试；format 自有套件；api 编码器往返测试；全一致性 + fourslash 在三平台 AOT 二进制上 CI 全绿。

### 2.4 边界上的精确裁定（易争议点逐条钉死）

1. **watch 编排属 M1，真实文件系统后端属 M2**。`internal/execute/watcher.go:17` 直接 import fswatch，但上游对 watch 的测试全部经 `WatchBackend` 抽象接口（internal/execute/watchmanager/watchbackend.go:12-15）注入 mock（internal/execute/tsctests/mock_watch_backend.go）。因此阶段 7 门禁（“Go execute 基线过”）**不依赖真实 fs 事件**：M1 移植 watch 编排 + WatchBackend 接口 + mock；FileSystemWatcher 真实后端按主计划归阶段 8。后果：M1 交付的二进制上 `--watch` 的端到端可用性要等阶段 8 后端就绪——该张力已列入 §10 待澄清第 2 条，请主计划裁定是否把最小真实后端前移。
2. **增量构建（.tsbuildinfo）属 M1；增量解析（AST 重用）是非目标**。两者英文都叫 incremental，勿混（证据见 §3.1）。
3. **nodebuilder / pseudochecker / evaluator 属 M1**（checker 域，d.ts 发射与 const enum 求值需要；主计划阶段 5 行明确写入 “checker 60k + evaluator/nodebuilder/pseudochecker ≈ 61k”）。
4. **testrunner/testutil 属 M1 阶段 3**（“同时移植 testrunner 0.9k + testutil”，主计划 §二）；**fourslash 属 M2 阶段 8**。
5. **tracing（`--generateTrace` 的 chrome trace）与 locale 属 M2 阶段 9**（主计划 §二 阶段 9 行）。M1 期间这两个旗标的行为（拒绝/忽略）由 06-module-inventory.md 裁定。
6. **M1 诊断仅英文**：diagnostics 消息表（2,154 条，`grep -c "code:" internal/diagnostics/diagnostics_generated.go` = 2154）在阶段 0 生成；但 internal/diagnostics/loc 的本地化目录随 locale 包归阶段 9。
7. **API 二进制编码器的代码生成（Api/Encoder.g.cs）在阶段 9 才产出**（主计划 §一.4 表），生成器脚手架本身阶段 0 建。

---

## 3. 非目标清单

每条给出上游证据。原则：**上游占位即我们占位；上游没有的我们不发明。** 以 tsgo 为唯一蓝本——strada（TypeScript 的 TS 自举实现）有而 tsgo 没有的行为一概不补。

### 3.1 不移植增量解析（AST 节点重用）

上游本身未实现，只留了占位注释。internal/parser/parser.go:1134-1138：

```go
if isAmbient {
    // !!! incremental parsing
    // node := p.tryReuseAmbientDeclaration(pos)
    // if node {
    // 	return node
    // }
```

`// !!!` 是 tsgo 代码库中「strada 有、此处待办/未移植」的统一标记（另见 internal/testrunner/test_case_parser.go:184 等处）。`tryReuseAmbientDeclaration` 在整个仓库中不存在实现。因此 C# 侧同样不实现；若日后上游实现了，随上游升级（§4.3）一起进来。**注意**：`.tsbuildinfo` 增量构建（internal/execute/incremental/）不受此条影响，属 M1 范围。

### 3.2 fswatch 不做逐行直译，基于 FileSystemWatcher 重写

`internal/fswatch` 是 tsgo 中唯一含**平台汇编**的包：为免 cgo，macOS 后端用 `//go:cgo_import_dynamic` + 汇编 trampoline 直接 FFI 调用 CoreFoundation/CoreServices/libdispatch（internal/fswatch/fsevents_darwin_ffi.go:17-29 的说明注释；fsevents_darwin_ffi.s、fsevents_darwin_ffi_amd64.s、fsevents_darwin_ffi_arm64.s）；Linux 后端有 fanotify_linux.go 与 inotify_linux.go 两套，另有 kqueue.go、windows.go 与一组 walkdir_*.go 平台目录遍历。这些机制（Go 汇编、go:cgo_import_dynamic、fanotify）在 .NET 中既不可直译也无必要——.NET 的 `System.IO.FileSystemWatcher` 本身就是对 FSEvents/inotify/ReadDirectoryChangesW 的跨平台封装。

**重写而非直译的保真边界**：保留 fswatch 的**对外语义面**——`Watcher` 接口、事件种类（event.go）、防抖聚合（debounce.go）、递归 watch 与 ignore 回调（watchbackend.go:13 的 `WatchDirectory(dir, fn, recursive, ignore)` 签名）、macOS 路径规范化语义（canonicalize_darwin.go 的 NFD 处理需给出 .NET 等价物）。上层（watchmanager、lspwatcher、project）照常直译。这是全项目**唯一一个允许实现自由度的包**，其测试策略（不能用字节 diff，改用行为测试移植 watcher_test.go/eventlist_test.go 语义）细节归 08-testing-strategy.md，风险登记归 12-risks.md。

### 3.3 不重写 VS Code 扩展与 npm 发布包装

`_extension/`（VS Code 扩展 “TypeScript 7 / native-preview”，_extension/package.json:2-4）与 `_packages/native-preview/`（npm 包 `@typescript/native-preview`）是独立的 TypeScript 工程，与编译器的耦合仅在两点：

1. 定位二进制：按文件名找 `tsgo`（_extension/src/util.ts:46-54）；
2. 驱动二进制：spawn `<exe> --lsp`，transport = stdio（_extension/src/client.ts:178-191）。

因此 C# 侧**只需保持二进制的 CLI 参数面与 LSP/API 线缆协议兼容**（§2.3），两个前端工程原样复用，不入本仓库、不重写、不维护分叉。同理 `_extension-nightly/` 不管。唯一注意点：扩展会设置 `GOMEMLIMIT` 环境变量（client.ts:170-176）——C# 二进制读到它时的行为（忽略或映射到 GC 配置）由 01-architecture.md 裁定，此处仅登记。

### 3.4 不移植 Go 工程基建

`Herebyfile.mjs`（Go 侧构建编排）、`_tools/`（golangci-lint 等）、`go.mod`/`go.sum`、`.github` 工作流按 .NET 生态重建（`dotnet build`/slnx/CI 见 01-architecture.md 与 10-phase-0-scaffolding.md），不做对应物翻译。`internal/pprof`（Go 运行时剖析开关）无直译意义，.NET 侧替代物（EventPipe/dotnet-trace）归 06-module-inventory.md 裁定（另见 §10 待澄清第 4 条）。

### 3.5 不修上游 bug，不做行为「改进」

bug-for-bug 兼容。上游自我标注的历史遗留问题也原样保留，例如 internal/pseudochecker/lookup.go:559-560：“strada treated unions and intersections the same … Likely a bug persisting from strada.”——照译，不修。任何「顺手修正」都会在基线 diff 中表现为噪声并摧毁验收货币。发现上游 bug 的正确动作：在 11-progress.md 登记、可选地给上游提 issue，代码保持一致。

### 3.6 不追求超越阶段 7 验收线的性能目标

性能验收只有一条硬线：阶段 7 AOT 门禁 ≤ 2× tsgo（主计划 §二）。阶段 10 的 BenchmarkDotNet 基准**不设门禁**（主计划 §三 08 条目），只做记录与回归观测。禁止为性能微调而偏离直译结构（例外须走 12-risks.md 登记 + 门禁证明行为不变）。

### 3.7 其余明确不做

- 不支持 .NET 10 / Native AOT 之外的部署形态承诺（测试项目跑 JIT 是内部手段，非交付物）。
- 不提供 strada（tsc.js/Node API）的 JS API 兼容层——JS 侧 API 由上游 `@typescript/native-preview` 的 lib/（经 `--api` 通道）承担，我们只保 `--api` 线缆兼容。
- 不移植 Go 特有可观测性（GOMEMLIMIT 语义、pprof HTTP 端点）；对应关系表归 01-architecture.md。

---

## 4. 上游钉扎策略

### 4.1 两级钉扎模型

正确性货币（基线）同时依赖两个上游坐标，缺一不可：

```
坐标 A：microsoft/typescript-go 的 commit  ──决定──▶  编译器行为、testdata/baselines 内容
坐标 B：microsoft/TypeScript 的 commit     ──决定──▶  conformance 测试用例源文件、diagnosticMessages.json、lib/*.d.ts
        （tsgo 以 gitlink 把 B 钉在 A 里）
```

tsgo 的 `.gitmodules`（/Users/token/Desktop/code/typescript-go-main/.gitmodules）：

```ini
[submodule "_submodules/TypeScript"]
	path = _submodules/TypeScript
	url = https://github.com/microsoft/TypeScript.git
	branch = tsgo-port
```

注意 `branch = tsgo-port` **不是钉扎**——它只影响 `git submodule update --remote` 追踪哪个分支（该分支是上游为 tsgo 维护的测试适配线）。**权威钉扎是父仓树对象中的 gitlink（mode 160000）记录的那枚 SHA**。

Go 代码在运行时按路径约定找子模块：internal/repo/paths.go:46-52 (`TypeScriptSubmodulePath()` = `<repo根>/_submodules/TypeScript`)，并以 `package.json` 是否存在判断子模块是否检出（paths.go:62-75，`SkipIfNoTypeScriptSubmodule` 用于测试跳过）。testrunner 直接从 `../_submodules/TypeScript/tests/cases/<suite>` 读 conformance 用例（internal/testrunner/compiler_runner.go:63）。**基线是对这些用例文件生成的：用例内容漂移 ⇒ 49,323 个基线的 diff 立即变成噪声。这就是「必须与 tsgo 的 pin 同 commit」的原因。**

**本仓库的铁律**：`typescript-csharp/_submodules/TypeScript` 是真 git submodule，检出的 commit 必须 == tsgo 快照（坐标 A）中 gitlink 记录的 commit（坐标 B）。任何时候二者不等，一致性测试结果一律视为无效。

### 4.2 pin 查询方法

**方法 1（标准，要求 tsgo 是 git 检出）**：

```bash
git -C <tsgo仓库> submodule status
# 输出示例： 4d4f005c8541e0255a9d8791205fdce326e462bc _submodules/TypeScript (v5.9-beta-...-g4d4f005c854)
```

输出含义（逐符号）：
- 首列 40 位 SHA = 子模块 commit；括号内为 `git describe` 结果，仅供人读。
- SHA 前缀 **空格** = 已初始化且工作区检出与父仓 gitlink 一致（健康态）；
- 前缀 **`-`** = 子模块未初始化（目录为空；SHA 显示的是父仓**记录**的 pin）；
- 前缀 **`+`** = 已检出但 **≠** 父仓记录（本地漂移，见 §4.4）；
- 前缀 **`U`** = 合并冲突。

或不依赖工作区状态、直接读树对象（更适合脚本）：

```bash
git -C <tsgo仓库> ls-tree HEAD _submodules/TypeScript
# 160000 commit <SHA>	_submodules/TypeScript      ← mode 160000 即 gitlink，<SHA> 即权威 pin
```

**方法 2（GitHub API，无需本地 git 元数据）**：

```bash
curl -s "https://api.github.com/repos/microsoft/typescript-go/contents/_submodules/TypeScript?ref=<tsgo-commit>" \
  | jq -r '.sha'         # type 字段为 "submodule"，.sha 即 gitlink SHA
```

**当前本地实况（重要）**：`/Users/token/Desktop/code/typescript-go-main` **不是 git 检出**（无 `.git`，方法 1 两条命令均报 `fatal: not a git repository`；目录名 `-main` 提示它是 GitHub zip 快照），且其 `_submodules/TypeScript/` 目录为空。因此：
- 坐标 B 只能经方法 2 查询。2026-07-11 对 `ref=main` 查得 gitlink = `4d4f005c8541e0255a9d8791205fdce326e462bc`（microsoft/TypeScript）。
- 但 zip 快照**无法自证**对应哪个 typescript-go commit（坐标 A 未知）⇒ 不能确认上述 SHA 就是本快照的 pin。这是 §10 待澄清第 1 条，**必须在阶段 0 解决**（建议以 `git clone` 固定 commit 替换 zip 快照）。

### 4.3 钉扎的落地与升级节奏

**落地（阶段 0，一次性）**：

```bash
cd /Users/token/Desktop/code/typescript-csharp
git submodule add https://github.com/microsoft/TypeScript.git _submodules/TypeScript
git -C _submodules/TypeScript fetch origin <坐标B-SHA> && git -C _submodules/TypeScript checkout <坐标B-SHA>
git add .gitmodules _submodules/TypeScript
```

同时在仓库根落一份 `UPSTREAM.lock`（纯文本，单一事实源），记录：坐标 A（tsgo commit + 快照来源）、坐标 B（TypeScript gitlink SHA）、testdata 同步指纹（同步日期 + 两个独立计数字段：`baselines/reference` 文件数与镜像总文件数/总字节，见 §5.2，裁决 R14）、代码生成输入副本（`tools/generator/ast.json`、schema.ts、diagnosticMessages.json、metaModel.json）的 sha256。所有校验脚本只信 lock 文件。（lock 文件的确切格式与校验脚本归 04-codegen.md / 10-phase-0-scaffolding.md 细化。）

**升级节奏**：
- **默认冻结**。里程碑 1 门禁全部签核前不升级上游，例外仅两种：(a) 上游修复了阻塞我们的 bug；(b) 需要上游新增基线扩大验收覆盖。
- **升级是原子操作**，单个 PR 内一次完成，禁止部分升级：
  1. 更换 tsgo 快照（坐标 A）；
  2. 按 §4.2 查询新 pin，移动 submodule 至新坐标 B；
  3. 全量重新同步 testdata（§5）；
  4. 重跑全部代码生成（ast.json/schema.ts、diagnosticMessages.json、108 个 lib、LSP metaModel——输入副本同步流程见 04-codegen.md）；
  5. rebase dumptool 补丁分支并重导出全部差分夹具（08-testing-strategy.md）；
  6. 更新 `UPSTREAM.lock`；
  7. 重跑当前已达阶段的**全部**门禁，差异逐条归因（上游行为变化 → 记 11-progress.md；我方缺口 → 修复后才可合并）。
  任何一步失败 ⇒ 整个 PR 回滚，仓库停留在旧 pin。
- 升级完成在 11-progress.md 记签核行（旧 pin → 新 pin、门禁重跑结果、归因清单）。

### 4.4 漂移处置流程

**漂移定义**：仓库中任意一处与 `UPSTREAM.lock` 不一致——包括 (i) submodule gitlink ≠ lock；(ii) `git submodule status` 出现 `+`/`U` 前缀；(iii) testdata 指纹不符；(iv) 生成器输入副本 sha256 不符；(v) 生成产物（*.g.cs）与重新生成结果不符。

**检测**：CI 每次构建先跑校验脚本（建议 `tools/check-upstream-pin.sh`，归阶段 0 交付）依次核对上述五项；本地开发者可随时手跑。任何一项失败即红灯，一致性测试结果作废。

**分类与处置**：

| 类别 | 症状 | 处置 |
|---|---|---|
| A. 本地误动 | submodule 被顺手 checkout 到别处（`+` 前缀）、testdata 被手改 | 机械回滚：`git -C _submodules/TypeScript checkout <lock-SHA>`；testdata 按 §5 重新 rsync。**从不**为迁就工作区而改 lock |
| B. 未走原子流程的升级 | lock 部分更新、门禁未重跑 | 二选一：补全 §4.3 的 7 步；或整体回滚到旧 pin。禁止带着半升级状态继续开发 |
| C. 上游不可达 | 上游 force-push / 分支删除导致 pin SHA 拉不到 | 用 GitHub API 按 SHA 直接取（commit 对象通常仍可达）；不行则从本地对象库/镜像恢复。预防：阶段 0 起对两个上游坐标各留一份完整 clone 归档（位置记入 lock 注释） |
| D. 有意接受上游行为差异 | 升级后某基线 diff 归因为上游行为变化 | 不存在“接受差异”选项——基线本身来自上游，升级后以新基线为准即可；若是 tsgo 快照与其自带基线不符（上游自身 CI 破损），停在旧 pin 等上游修复 |

---

## 5. testdata / 基线同步策略

### 5.1 内容盘点（2026-07-11 对本地快照实测）

| 路径（tsgo 快照内） | 实测 | 用途 |
|---|---|---|
| `testdata/baselines/reference/` | **49,323 个文件、282 MB**（`find … -type f | wc -l`、`du -sh`；testdata 全目录 283 MB） | 验收货币：conformance 期望输出（.errors.txt/.types/.symbols/.js/.d.ts/.js.map 等族） |
| `testdata/fixtures/` | 若干 | Go 单测数据（随各包测试移植） |
| `testdata/tests/cases/` | 319 个文件 | tsgo 自增测试用例（testrunner 的第二来源，compiler_runner.go:65） |
| `testdata/submoduleAccepted.txt` | 1,426 行 | tsgo 相对 strada 基线的**已接受**差异清单（文件头注释：“Diff files to instead write to submoduleAccepted as ‘accepted’ changes.”） |
| `testdata/submoduleTriaged.txt` | 87 行 | 同上，已分诊待处理清单 |

conformance 用例**源文件**不在 testdata 里——在 `_submodules/TypeScript/tests/cases/`（§4.1）。所以 testdata 同步与 submodule 钉扎必须同频。

### 5.2 同步机制

- **方式**：本地 rsync，整目录镜像（含删除）：

  ```bash
  rsync -a --delete /Users/token/Desktop/code/typescript-go-main/testdata/ \
                    /Users/token/Desktop/code/typescript-csharp/testdata/
  ```

  `testdata/` 保持 tsgo **纯镜像**——我方自有夹具（dumptool 差分夹具、jsnum 向量等）一律放仓库根 `fixtures/`（`fixtures/*.tar.zst`、`fixtures/jsnum/`、`fixtures.lock.json` 均在 `fixtures/` 下），因此 rsync 无需任何 exclude（裁决 R14）。同步后更新 `UPSTREAM.lock` 中的 testdata 指纹——**分开记录两个字段**：`baselines/reference` 文件数（49,323）与 testdata 镜像总文件数/总字节（校验脚本分别核对；同为 R14）。建议把这两步包成 `tools/sync-testdata.sh`（阶段 0 交付，归 10-phase-0-scaffolding.md 排 WBS）。
- **入库**：同步结果**提交进 git**——与主计划 §一.3 仓库结构一致（`testdata/baselines/reference/` 是仓库的一部分），也与上游做法一致（tsgo 自己就提交全部基线）。49,323 个小文本文件对 git 完全可行。
- **时机**（仅三种，其余时间只读）：
  1. 阶段 0 初次同步；
  2. §4.3 上游升级原子 PR 内全量重同步；
  3. §4.4 类别 A 漂移的恢复性重同步。
- **只读纪律**：`reference/` 目录任何手工编辑一律禁止。diff 失败只能通过修 C# 代码收敛；不存在「改基线让测试过」的操作。跑批器把我方实际输出写到 `testdata/baselines/local/`（与 tsgo testrunner 同构）供 diff 与调试，`local/` 加入 `.gitignore`。accept 工作流仅是 tsgo 上游开发者维护其基线的机制，我方跑批器只需**理解** `.diff` 文件与 submoduleAccepted/Triaged 清单的语义（我们对齐的 reference 已包含这些已接受差异）——细节归 08-testing-strategy.md。

---

## 6. 交付物

### 6.1 立即交付（本步，阶段 0 之内）

- `plan/` 全部 13 份文档（本文档为 00 号；清单见 §7 与 §9）。其中 09-phases.md **补写中**（含门禁稳定 ID `G<阶段>.<序号>` 体系；裁决 R20，引用照常保留）。
- 解决方案脚手架：`TypeScript.slnx`、`Directory.Build.props`/`Directory.Packages.props`、三源项目 + 五测试项目骨架、CI、submodule、`UPSTREAM.lock`、生成器自举（ast + diagnostics + libs）、`LargeStackExecutor`（主计划 §二 阶段 0 行）。

### 6.2 里程碑 1 交付

- 三平台（win/linux/osx，x64+arm64 按 CI 矩阵定，见 01-architecture.md）`PublishAot=true` 的 `Ts.Cli` 二进制：tsc 全功能（§2.2），`--lsp`/`--api` 占位。
- conformance 六族基线 100% 字节匹配；`.tsbuildinfo` 字节兼容；4-checker 并行 = 单 checker 输出全等。
- `tools/generator/`（TS 写的全部生成器 + 入库产物）、`tools/dumptool/`（tsgo 补丁分支）+ 全语料差分夹具。

### 6.3 里程碑 2 交付

- 同一二进制补全 `--lsp`（LSP 3.17 + tsgo 扩展，官方 `_extension` 直接可用）与 `--api`（msgpack 帧兼容，`@typescript/native-preview` 直接可用）。
- fourslash 99%+、LSP 回放、format 套件、api 往返全绿；三平台 AOT CI 全绿（阶段 10）。

### 6.4 持续交付（活文档）

- 11-progress.md：每包完成度、一致性通过率历史、门禁签核日志。
- 12-risks.md：风险册（每条附缓解 + 捕获它的门禁）。

---

## 7. 仓库结构图（照主计划 §一.3）

```
typescript-csharp/
  TypeScript.slnx
  Directory.Build.props        net10.0; Nullable enable; ImplicitUsings off;
                               InvariantGlobalization=true; IsAotCompatible+AOT/Trim analyzers 全开
  Directory.Packages.props     集中版本管理（System.IO.Hashing, System.Threading.Channels, xunit.v3…）
  UPSTREAM.lock                两级 pin + testdata/生成输入指纹（§4.3；本文档新增的操作性文件）
  _submodules/TypeScript/      与 tsgo 钉扎到同一 commit 的官方仓库 submodule（§4）
  plan/                        本计划的 13 份文档（00–12；09 补写中，R20）
  tools/generator/             TS 写的代码生成器（ast/diagnostics/lsp/libs/encoder/stringers）
  tools/dumptool/              对 tsgo 的补丁分支：导出 token/AST/symbol 夹具（UTF-16 位置）供差分测试
  src/Ts.Compiler/             Core/ Collections/ TsPath/ Vfs/ JsNum/ StringUtil/ Scanner/ Ast/ Parser/
                               Binder/ Checker/ Printer/ Transformers/ SourceMap/ Module/ TsOptions/
                               Diagnostics/ Compiler/ Bundled/(108 个 lib.d.ts EmbeddedResource) 等
  src/Ts.LanguageService/      Ls/ Format/ Lsp/ Project/ Api/ FsWatch/ JsonRpc/   → 引用 Ts.Compiler
  src/Ts.Cli/                  tsgo 等价二进制，PublishAot=true（默认 tsc / --lsp / --api 三路复用）
  tests/Ts.TestUtil/ Ts.Compiler.Tests/ Ts.Conformance.Tests/ Ts.Fourslash.Tests/ Ts.LanguageService.Tests/
  testdata/baselines/reference/  从 tsgo 同步的 49,323 个基线文件（正确性的「货币」，§5）
  testdata/…                     fixtures / tests/cases / submoduleAccepted.txt / submoduleTriaged.txt（§5.1）
```

映射规则（详见 05-conventions.md 与 06-module-inventory.md）：Go 包 → 命名空间（`internal/ast` → `Ts.Ast`）；Go 文件 → PascalCase 同名 .cs 文件；>15k 行巨型文件按 Go 原有分节注释拆 partial class（如 checker.go 31,926 行 → Checker.cs / Checker.Relations.cs / …，拆分表在 06）。

---

## 8. 术语表

按主题分组。带路径的条目给出上游定义点。

### 8.1 上游与生态

| 术语 | 含义 |
|---|---|
| **tsgo** | microsoft/typescript-go 仓库及其单一二进制名（cmd/tsgo/）。TypeScript 编译器的 Go 原生移植，即本项目的唯一蓝本。发行名 “TypeScript 7 / native-preview” |
| **strada** | TypeScript 自举实现（microsoft/TypeScript，TS 写的 tsc/tsserver）的内部代号。tsgo 源码注释里频繁出现，指「老实现的行为」（如 internal/pseudochecker/checker.go:5、lookup.go:270） |
| **`// !!!`** | tsgo 源码中「strada 有此逻辑、Go 版尚未移植/刻意占位」的统一标记（如 parser.go:1134 增量解析）。移植规则：占位照抄占位 |
| **_submodules/TypeScript** | tsgo 内嵌的 strada 仓库 submodule（分支 tsgo-port），提供 conformance 用例源文件、diagnosticMessages.json、lib/*.d.ts（§4.1） |
| **_extension / native-preview** | 官方 VS Code 扩展（_extension/）与 npm 包 `@typescript/native-preview`（_packages/native-preview/），经 `--lsp`/bin 包装驱动二进制；非移植对象（§3.3） |
| **gitlink / pin（钉扎）** | git 父仓树中 mode 160000 的条目，把 submodule 钉在确定 commit。本项目的两级钉扎见 §4.1 |
| **漂移（drift）** | 仓库任一上游耦合物（submodule/testdata/生成输入/生成产物）偏离 `UPSTREAM.lock` 的状态（§4.4） |

### 8.2 验证体系

| 术语 | 含义 |
|---|---|
| **conformance** | 官方兼容性测试语料：`_submodules/TypeScript/tests/cases/{conformance,compiler}` 下数万 .ts 用例，由 testrunner 驱动 |
| **基线（baseline）** | 对每个用例预期输出的入库快照，按扩展名分族：`.errors.txt`（诊断）、`.types`/`.symbols`（逐表达式类型/符号）、`.js`/`.d.ts`（发射）、`.js.map`/`.sourcemap.txt`（源映射）、tsbuild 相关。`reference/` = 期望，`local/` = 本次实际输出（不入库） |
| **testrunner** | tsgo 的 conformance 跑批器（internal/testrunner/），阶段 3 移植；读用例、编译、写 local、与 reference diff |
| **accept 工作流** | 上游用 local 覆盖 reference 以接受行为变化的流程。本项目 reference 只读，不使用 accept 更新（§5.2） |
| **submoduleAccepted/Triaged.txt** | tsgo 记录其输出相对 strada 基线已接受/已分诊差异的清单（testdata/ 下，1,426/87 行）。背景知识，diff 语义详见 08 |
| **夹具（fixture）** | 差分测试的中间产物：dumptool 从 tsgo 导出的 token 流/AST 结构/symbol 转储文件，作为阶段 2–4 的 oracle（位置已预转 UTF-16） |
| **差分测试（differential testing）** | 同一输入分别喂给 tsgo（经 dumptool/基线）与 C# 实现，对输出做逐字节 diff 的验证方法。本项目一切验收的形态 |
| **dumptool** | 我方维护的 tsgo **补丁分支**工具（tools/dumptool/），批量导出夹具；C# CI 不需要 Go 工具链，只消费导出产物（主计划 §二 导语） |
| **门禁（gate）** | 阶段出口的硬性验收条件（主计划 §二 表）。签核记录在 11-progress.md |
| **fourslash** | strada 传下来的语言服务测试 DSL（文件内 `////` 标记 + 验证脚本），tsgo 在 internal/fourslash/；阶段 8 的主要验收 |
| **readable baseline** | tsbuild 测试对 .tsbuildinfo 的人类可读转写（internal/execute/tsctests/readablebuildinfo.go），2 空格缩进 + size 字段；测试基架需复刻（主计划 §一.5） |

### 8.3 架构与实现

| 术语 | 含义 |
|---|---|
| **mixin（数据 mixin）** | ast.json 中被多个节点复用的字段组（13 个，如 DeclarationBase/ModifiersBase）。C# 表示为同名接口（**不加 I 前缀**，规范例外）+ 字段扁平化 + Node 上的虚访问器（主计划 §一.1；详见 02） |
| **brand base** | ast.json 中无字段的纯分类基（4 个：TypeSyntaxBase 等）→ C# 纯标记接口（02） |
| **Node 系 base** | ast.json 里携带 Node 头血缘的 18 个基类，构成 C# 单继承链；与 mixin/brand 零交叉（02） |
| **驻留（interning）** | checker 对结构化类型「同键同实例」的缓存构造：先算结构键再查表，命中返回既有 Type（如 checker.go:658 `unionTypes map[CacheHashKey]*Type`、:620 `stringLiteralTypes`）。指针相等因此承载语义，是 03 的核心不变量 |
| **CacheHashKey** | xxh3.Uint128 构成的结构键类型 → C# `readonly record struct CacheHashKey(UInt128)`（主计划 §一.6） |
| **LinkStore** | `LinkStore[K comparable, V any]`（internal/core/linkstore.go:5）：以节点/符号指针为键挂检查期附加数据 → C# ReferenceEqualityComparer 字典包装（03） |
| **checker 池** | 并行检查机制：默认 4 个 Checker、文件按序号静态钉扎、每 checker 一把锁（internal/compiler/checkerpool.go:41-53）。类型永不跨 checker 共享（Type.checker 反向指针），确定性要求单/多 checker 输出全等 |
| **WorkGroup** | tsgo 的并行/串行两态任务组抽象；C# 版两实现共用 LargeStackExecutor（01） |
| **LargeStackExecutor** | 0 号风险的对策：parse/bind/check 全部跑在 64MB 大栈专用线程上（.NET 默认 1MB 线程栈扛不住 checker 深递归；对应 tsc.js 的 --stack-size）（主计划 §一.5；01） |
| **arena** | Go 侧的批量分配器（~50 处）。C# 全部删除，`arena.New()` → `new T()`，靠 gen0 GC（主计划 §一.1） |
| **PositionMap** | tsgo 内部 UTF-8↔UTF-16 位置换算层。C# 版**整体删除**：一切位置即 UTF-16 code unit（与 strada 一致）；仅测试基架留两个垫片（主计划 §一.6；01） |
| **Native AOT / Trim** | .NET 提前编译为原生二进制 + 裁剪。铁律（禁反射、STJ 仅源生成等）见 07 |
| **STJ 源生成** | System.Text.Json 的编译期序列化器生成（JsonSerializerContext）；反射序列化器封禁（07） |
| **.tsbuildinfo** | 增量构建状态文件。字节级保真规格（键序/omitzero/多态形状/UnsafeRelaxedJsonEscaping/xxh3 签名）见主计划 §一.5 与 01 |
| **pseudochecker** | tsgo 中不依赖完整 checker 的轻量类型推导（internal/pseudochecker/），供隔离声明发射等；阶段 5 一并移植 |
| **nodebuilder / evaluator** | checker 域配套：类型 → 类型节点重建（d.ts 发射）；常量表达式求值（const enum）。均属 M1 |
| **bundled lib** | 108 个 `lib.*.d.ts`（internal/bundled/libs/，实测 108 个）→ C# EmbeddedResource + `bundled:///` VFS 覆盖 |
| **metaModel** | LSP 3.17 官方元模型 JSON（~17k 行），generate-lsp.ts 的输入（04） |
| **jsnum** | JS Number 语义包（internal/jsnum）：ES 数字格式化/ToInt32 等。阶段 1 有专属百万向量门禁 |

---

## 9. 阅读指南

### 9.1 建议阅读顺序（新加入的移植工程师）

1. **00（本文档）** —— 边界、货币、上游关系。
2. **05-conventions.md** —— Go→C# 惯用法圣经。动手写任何一行前必读，日常查阅频率最高。
3. **01-architecture.md** —— 程序集/线程/内存/JSON 保真/AOT 管线的全局图。
4. **06-module-inventory.md** —— 找到你负责的包：路径映射、LOC、阶段、危险点。
5. **09-phases.md + 10-phase-0-scaffolding.md** —— 当前阶段的进入/退出条件与文件级 WBS。
6. 按分工深入专题：AST → 02；checker → 03；代码生成 → 04；测试 → 08。
7. **07-aot-constraints.md** 在首次提交前过一遍禁用清单；**12-risks.md** 了解已知深坑；**11-progress.md** 看当前水位。

### 9.2 「哪个问题查哪份文档」导航表

| 你的问题 | 查 |
|---|---|
| 这个 Go 写法（error 返回/defer/nil 切片/map 迭代/goroutine/类型 switch/nil 接收者）翻成什么 C#？ | 05-conventions.md |
| getter 命名？mixin 接口为何不加 I？巨文件拆分到哪些 partial？ | 05-conventions.md（拆分表另见 06） |
| 某个 `internal/xxx` 包归哪个项目/命名空间/阶段？有什么危险点（反射/unsafe/平台特定）？ | 06-module-inventory.md |
| AST 节点类怎么生成？mixin 访问器长什么样？NodeFactory/钩子？ | 02-ast-design.md |
| Type 继承树？某个缓存键的 C# 签名？驻留/关系缓存？并发确定性证明义务？递归深度？ | 03-checker-design.md |
| 生成器怎么跑？输入副本（ast.json/metaModel/diagnosticMessages）归谁管、怎么随升级同步？ | 04-codegen.md |
| 哪些 API 被禁？MSBuild 属性套餐？如何本地验证 AOT？枚举 ToString 政策？ | 07-aot-constraints.md |
| 夹具格式？dumptool 怎么再生成？conformance 跑批器与 diff 工作流？fourslash 策略？ | 08-testing-strategy.md |
| 各阶段进入/退出条件、依赖 DAG、阶段 5/8 的分级子门禁？ | 09-phases.md |
| 当前阶段到底建哪些文件、什么顺序？ | 10-phase-0-scaffolding.md（滚动更新） |
| 现在整体进度/某门禁签核没有？ | 11-progress.md |
| 这个坑（栈溢出/map 序/浮点格式/xxh3/Windows 路径…）有没有人想过？ | 12-risks.md |
| 线程模型/内存模型/UTF-16 决策依据/JSON 字节保真/AOT 发布管线？ | 01-architecture.md |
| 上游 commit 怎么查/怎么升级/testdata 何时同步/某术语什么意思/里程碑边界？ | 00（本文档） |

### 9.3 文档间关系约定

- 主计划（precious-riding-hoare.md）是宪法：13 份文档只展开、不推翻。发现矛盾 → 写入各自「待澄清」节，等主计划裁定。
- 交叉引用一律用相对文件名（如 `05-conventions.md`），不用绝对路径。
- 11、12 为活文档随时更新；其余文档改动需升版本号并在文首元信息记录。

---

## 10. 待澄清

以下事项不改设计、只登记，请主计划维护者裁定：

1. **本地 tsgo 快照的 commit 身份未知（阶段 0 阻塞项）**。`/Users/token/Desktop/code/typescript-go-main` 无 `.git`（zip 快照），无法执行 §4.2 方法 1，坐标 A 不可自证；其 `_submodules/TypeScript/` 为空目录，坐标 B 也只能对上游 GitHub 查询（2026-07-11 对 `ref=main` 查得 `4d4f005c8541e0255a9d8791205fdce326e462bc`，但无法确认与本快照对应）。**建议**：用 `git clone` + checkout 固定 commit 替换 zip 快照（或经 CHANGES.md/文件指纹比对锁定快照 commit），随后立刻落 `UPSTREAM.lock`。
2. **fswatch 的阶段归属张力**。主计划 §二把 fswatch 归阶段 8，但 `internal/execute/watcher.go:17` import fswatch——阶段 7 的 watch 编排编译期就需要它的类型（`fswatch.EventKind`、`fswatch.WatchCallback` 出现在 watchmanager 接口签名，watchbackend.go:13）。本文档 §2.4.1 的处置（阶段 7 移植接口 + mock，真实后端阶段 8）满足阶段 7 门禁，但意味着 M1 二进制的 `--watch` 端到端不可用。是否把最小 FileSystemWatcher 后端前移进阶段 7，请裁定。
3. **主计划内部计数不一致：「21 类型表」vs「24 个 type struct」**。§三 03-checker-design.md 条目写“21 类型表”，而 §一.2 说“24 个 type struct（20 具体 + 4 纯抽象中间层）”。疑为笔误（20+4=24；“21”或为“Type 本身 + 20 具体”的口径）。03 撰写时需统一口径。
4. **「41 包」口径**。主计划 §三 06 条目称“41 包逐一”，但 `internal/` 顶层实有 48 个目录（api ast astnav binder bundled checker collections compiler core debug diagnostics diagnosticwriter evaluator execute format fourslash fswatch glob jsnum json jsonrpc locale ls lsp module modulespecifiers nativepath nodebuilder outputpaths packagejson parser pprof printer project pseudochecker repo scanner semver sourcemap stringutil symlinks testrunner testutil tracing transformers tsoptions tspath vfs）。若 41 = 剔除测试/辅助类包（fourslash/testrunner/testutil/repo/debug/pprof/json 恰 7 个）则口径吻合，请 06 给出精确清单并注明剔除项的处置（移植/替代/不移植）。
5. **`internal/pprof` 与 `GOMEMLIMIT` 的对应物未在主计划出现**。建议：pprof 归 06 裁定（倾向不移植，以 EventPipe/dotnet-trace 替代观测能力）；`GOMEMLIMIT`（官方扩展会设置它，_extension/src/client.ts:170-176）的读取/忽略行为归 01 裁定。
6. **`UPSTREAM.lock` 与校验/同步脚本是本文档为落实 §4/§5 新增的操作性机制**（主计划仅要求“钉扎策略”本身）。文件名、格式与脚本归属（04 还是 10）请在相应文档定稿时确认，如与其安排冲突以彼为准。
