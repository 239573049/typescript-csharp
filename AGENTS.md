# AGENTS.md — typescript-go → C#/.NET 10 (Native AOT) 移植作业手册

> 本文件是所有执行 Agent 的**开工必读**。它定义工作循环、纪律与权威文档。
> 计划本体在 `plan/`（13 份文档）；本手册只告诉你**怎么干活**，不重复计划内容。

## 0. 使命一句话

把 `/Users/token/Desktop/code/typescript-go-main`（微软官方 Go 版 TypeScript 编译器 tsgo，生产代码约 30 万行）1:1 复刻为本仓库的 C#/.NET 10 Native AOT 实现：同样的规则、同样的算法、同样的架构，以官方 conformance 基线逐字节 diff 为唯一验收货币。

## 1. 仓库拓扑

| 路径 | 角色 | 纪律 |
|---|---|---|
| `/Users/token/Desktop/code/typescript-go-main` | Go 源仓库（移植的蓝本） | **只读**。任何时候不得修改（dumptool 例外，见 plan/08 §2 的补丁分支规程） |
| 本仓库 `/Users/token/Desktop/code/typescript-csharp` | C# 目标仓库 | 你的全部工作发生在这里 |
| `plan/00…12` | 计划文档集（13 份） | 唯一事实源；与代码冲突时**以文档为准**，除非走偏差登记流程（§6） |

## 2. 权威文档表 —— 哪个域谁说了算

遇到两份文档口径不一时，按此表裁决（这是评审后的定稿裁决，不要重新争论）：

| 域 | 权威 |
|---|---|
| 范围/里程碑/上游钉扎/术语 | plan/00-overview.md |
| 程序集布局、线程模型（checker 池/WorkGroup/LargeStackExecutor）、RequestContext、内存模型、Ts.Core.ObjectPool | plan/01-architecture.md |
| AST 类层次、mixin→接口+虚访问器、生成面（**7 个 .g.cs** 定稿）、NodeFactory | plan/02-ast-design.md |
| Type 层次（24 struct）、**checker.go 拆分表（22 个 partial，唯一权威 §4.2）**、LinkStore/驻留缓存、并发确定性 | plan/03-checker-design.md |
| 代码生成七族、tools/generator 工作区布局、npm scripts、expected-counts | plan/04-codegen.md |
| Go→C# 惯用法（命名/error/切片/map/字符串/并发映射…） | plan/05-conventions.md |
| 41 包→文件映射清单、阶段归属 | plan/06-module-inventory.md |
| AOT 禁令、MSBuild 属性（发布旋钮初始不设）、STJ 源生成政策、内嵌资源 LogicalName | plan/07-aot-constraints.md |
| 测试分类学、dumptool/夹具规格、conformance runner、fourslash、性能语料 | plan/08-testing-strategy.md |
| 阶段进入/出口门禁（稳定编号 G<阶段>.<序号>）、重叠 DAG、阶段 5/8 分级子门禁 | plan/09-phases.md |
| 阶段 0/1 文件级 WBS | plan/10-phase-0-scaffolding.md |
| 当前进度、门禁签核日志（**每次会话必须更新**） | plan/11-progress.md |
| 风险册 R01–R23 | plan/12-risks.md |

已定稿的关键裁决（散见各文档，此处防重开争论）：AST 生成物 = 7 个 .g.cs（含独立 SyntaxKind.g.cs 与 GlobalUsings.g.cs）；**JSDoc 拼写整体保留**（不改 JsDoc）；生成的 Is* 守卫落 `Ts.Ast.NodeTests`；debug 包吸收为 `Ts.Core.Debug`；`sync.Pool → Ts.Core.ObjectPool<T>`（禁用 Microsoft.Extensions.ObjectPool）；单线程开关 `TSCS_SINGLE_THREADED=1`；自有夹具放仓库根 `fixtures/`，`testdata/` 是 tsgo 纯镜像；执行入口一律经 `LargeStackExecutor`（64MB 栈）。

## 3. 每次会话的标准工作循环

1. **定位**：读 `plan/11-progress.md` 找当前阶段与下一个未认领工作项；对照 `plan/09-phases.md` 确认所处阶段的进入条件已满足、出口门禁是什么。阶段 0/1 的文件级 WBS 在 plan/10。
2. **备课**：打开 `plan/06-module-inventory.md` 里目标包的小节（文件映射、危险点、依赖）；通读 `plan/05-conventions.md` 与本次文件相关的章节；若涉及 AST/Checker/代码生成，再读 02/03/04 的对应节。
3. **移植**：Go 文件 → 同名 C# 文件（PascalCase），**逐函数直译**。保持函数顺序与 Go 原文件一致；Go 原文注释一并移植；`// !!!` 标记原样保留。不确定的语义（map 迭代序、nil 语义、整型宽度）查 05，还不确定就去读 Go 源码上下文，仍不确定则按 §6 登记而不是猜。
4. **测试**：同步移植该文件的 `*_test.go`（映射规则见 05 §16）；有差分夹具的（阶段 2 起）跑对应夹具族。
5. **验证**（顺序执行，全绿才算完）：
   ```sh
   dotnet build TypeScript.slnx           # 0 警告 0 错误（TreatWarningsAsErrors 已开）
   dotnet test  TypeScript.slnx           # 全绿
   # 触碰 src/ 的会话，加跑 AOT 冒烟：
   dotnet publish src/Ts.Cli/Ts.Cli.csproj -c Release -r osx-arm64
   ```
6. **记账**：更新 `plan/11-progress.md`（包状态、通过率、变更日志一行）；若触及门禁，按 09 的门禁 ID 在签核表记录证据。
7. **提交**：每个自洽的工作单元一个 commit，消息格式 `port(<包名>): <go 文件> → <cs 文件>`（如 `port(core): text.go → Text.cs`）；文档修订用 `plan: …`；生成器/生成物用 `gen: …`。**不要 push**（无远端）。

## 4. 铁律（违反即返工）

1. **不发明架构**。所有设计决策已在 plan/ 定稿；你的自由度只在函数内部的直译表达上。
2. **AOT 禁令时刻生效**（plan/07）：禁反射（typeof/枚举 ToString 除外）、禁 STJ 反射序列化器、禁 culture 敏感字符串 API（analyzer 会拦，绿了才算数）。
3. **位置即 UTF-16 code unit**。任何从 Go 移植来的"字节偏移"概念都要重新审视（plan/01 §UTF-16 与 plan/08 的两个垫片是仅有的例外面）。
4. **每个 `for range m`（map 迭代）移植点必须带标注** `// map-iter: 序无关` 或 `// map-iter: 已排序`（05 §7.3，CI 会正则抽查）。
5. **`arena.New()` → `new T()`**，不引入池化；仅 parserPool/binderPool 等 plan/01 §5.2 列名的池用 `Ts.Core.ObjectPool<T>`。
6. Go nil 接收者方法 → **扩展方法**；`(T, error)` 返回 → 按 05 §4 的三分判据（nullable / Try 模式 / 异常），不得自创第四种。
7. **测试基线不可为过而改**。基线 diff 不过 = 你的代码有错；确认是 tsgo 侧原因才走 §6 偏差登记。
8. 深递归入口必须经 `LargeStackExecutor.Run`（新增公共入口时检查一遍）。
9. 生成代码（*.g.cs）**只能由生成器产出**，手改无效（CI 新鲜度检查会红）；要改就改 tools/generator 并重新生成。
10. 会话结束时仓库必须是绿的（能 build、能 test）。做不完的工作宁可整体不落盘，也不留半截红仓库。

## 5. 验收货币

一切"完成"的定义都是**与 tsgo 输出逐字节相等**，按阶段递进：token 流 → AST 转储 → symbol/flow → `.errors.txt` → `.types`/`.symbols` → `.js`/`.d.ts`/`.js.map` → `.tsbuildinfo` → fourslash。门禁编号与测量命令见 plan/09；此外单线程模式与 4-checker 并行模式的输出必须全等（G 门禁内置此项）。

## 6. 偏差登记流程

发现 plan/ 文档错误、内部矛盾、或确有必要偏离 Go 行为时：
1. 不要静默偏离。在受影响文档的「待澄清」节登记（引用证据 file:line）；
2. 行为偏差另在 `plan/11-progress.md` §已知偏差表登记一行；
3. 风险级别的问题追加到 `plan/12-risks.md`（沿用 R 编号）；
4. 然后按你登记的口径继续干活——登记本身就是授权，不需要等待批准。

## 7. 会话分派模板

给执行 Agent 的任务指令建议采用如下格式（花括号由分派人填写）：

```
读取仓库根 AGENTS.md 并严格遵守其工作循环与铁律。
本次任务：{阶段 N} 的 {包名/文件组}，具体为 {internal/xxx/yyy.go → src/Ts.Compiler/Xxx/Yyy.cs, ...}。
范围之外的文件一律不碰。完成定义：{对应门禁/测试口径，如「移植的 Go 单测全绿 + build 0 警告」}。
结束前必须完成 AGENTS.md §3 第 5-7 步（验证、记账、提交）。
```

不指定具体文件时，Agent 应自行执行 §3 第 1 步从 plan/11 认领下一项。

## 8. 当前状态快照（由会话维护，过期以 plan/11 为准）

- 阶段 0 进行中：解决方案脚手架已就位（slnx + 3 src 项目 + 5 测试项目，build/test/AOT publish 全绿；LargeStackExecutor 已落地并有 5 个单测）
- plan/ 13 份文档：00–08、10–12 已定稿（经三镜头评审 + 裁决修复）；09-phases.md 补写中
- 尚未开始：tools/generator 自举、_submodules/TypeScript 子模块添加、testdata 镜像同步、CI 工作流、dumptool
