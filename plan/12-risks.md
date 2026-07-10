# 12 — 风险册（Risk Register）

| 元信息 | |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-07-11 |
| 状态 | Draft |
| 主计划 | `~/.claude/plans/precious-riding-hoare.md`（已批准；本文档是其 §三 文档集中的 12 号） |
| 上游快照 | `/Users/token/Desktop/code/typescript-go-main`（tsgo） |
| 交叉引用 | [00-overview.md]（钉扎策略）、[01-architecture.md]（线程/内存/JSON 策略）、[03-checker-design.md]（结构键/并发）、[05-conventions.md]（惯用法逐案表）、[07-aot-constraints.md]（analyzer 门禁）、[08-testing-strategy.md]（差分测试与 conformance 跑批）、[09-phases.md]（阶段门禁）、[11-progress.md]（签核日志） |

本文档是全移植周期的**风险登记与追踪台账**。每一条风险都必须回答同一个问题：**如果它发生了，哪一个自动化门禁或 analyzer 会先于人眼发现它？** 没有检测手段的缓解措施不算缓解。

---

## 0. 使用说明

**字段定义**：

- **ID**：`R01…Rnn`，只增不改。风险关闭后 ID 保留（含处置记录），不复用。
- **影响**：发生后的损失量级。高 = 门禁大面积失败 / 数据（基线、.tsbuildinfo）损坏 / 进程崩溃 / 里程碑级返工；中 = 局部返工、单个门禁受阻、进度损失以周计；低 = 开发体验或非门禁指标劣化。
- **概率**：不采取缓解措施时在移植周期内发生的可能性（高 / 中 / 低）。
- **缓解措施**：编号列表；凡引用其他计划文档的规则，以相对文件名标注。
- **检测手段**：明确到**具体阶段门禁**（[09-phases.md] 编号——09 由独立 agent 补写中，将含门禁稳定 ID `G<阶段>.<序号>` 体系，裁决 R20；定稿前以阶段门禁描述指代）、**具体 analyzer 规则**（[07-aot-constraints.md]）或具体测试套件。
- **状态**：`开放` → `缓解中`（措施已落地部分）→ `已缓解`（检测手段验证过至少一次）→ `已关闭` / `已接受`（残余风险明确记录）。本版全部初始为**开放**。

**登记规则**：移植中发现新风险时，在本文档追加条目并在总览表登记，同时在 [11-progress.md] 的门禁签核日志里引用其 ID。风险状态变更需同步更新两处。

**类别缩写**：`SEM` 语义保真、`PERF` 性能与资源、`INFRA` 工程基础设施、`PROC` 流程与外部依赖。

---

## 1. 风险矩阵总览

| ID | 标题 | 类别 | 影响 | 概率 | 主检测手段 | 状态 |
|---|---|---|---|---|---|---|
| R01 | 栈溢出：.NET 固定栈 vs Go 增长栈 | SEM | 高 | 高 | 阶段 3/5 全语料零崩溃 + conformance 深递归样例 | 开放 |
| R02 | Go int（64 位）→ C# int（32 位）溢出 | SEM | 中 | 中 | 阶段 1 jsnum 门禁 + 各阶段差分夹具 + 05 整型宽度逐案表评审 | 开放 |
| R03 | map 迭代序依赖（Go 随机序 ↔ C# 插入序，双向陷阱） | SEM | 高 | 高 | 阶段 5 终检（4-checker 并行 ≡ 单 checker）+ 迭代序审计规则 | 开放 |
| R04 | nil slice/map 读语义差 → C# NRE | SEM | 中 | 高 | 阶段 3 全语料零崩溃 + Nullable analyzer + omitzero 字节 diff | 开放 |
| R05 | 浮点→字符串：ES6 版式与最短往返 | SEM | 高 | 中 | 阶段 1 jsnum 门禁（100 万随机 double 逐位相等） | 开放 |
| R06 | xxh3 必须哈希 UTF-8 字节（.tsbuildinfo 承重） | SEM | 高 | 中 | 固定测试向量单测 + 阶段 7 .tsbuildinfo 字节相等 | 开放 |
| R07 | JSON 转义与键序的字节保真 | SEM | 高 | 高 | 阶段 7 .tsbuildinfo 字节 diff + readable-baseline diff | 开放 |
| R08 | Go regexp（RE2 线性）vs .NET Regex（回溯）语义/性能差 | SEM | 中 | 中 | 阶段 4 模块解析 trace、阶段 8 fourslash autoImport、07 政策评审 | 开放 |
| R09 | errors.txt 码点垫片与 sourcemap UTF-8 字节长垫片 | SEM | 中 | 高 | 阶段 5 errors.txt 100% 字节匹配 + 阶段 6 sourcemap.txt 基线 | 开放 |
| R10 | InvariantGlobalization 与 JS 精确大小写 | SEM | 高 | 中 | 阶段 5 .types 基线（intrinsic 映射样例）+ CA13xx analyzer | 开放 |
| R11 | Windows 路径与大小写敏感性探测 | SEM | 中 | 中 | 阶段 1 tspath/vfs 移植测试 + 三平台 CI 矩阵 | 开放 |
| R12 | 上游漂移（tsgo 持续开发） | PROC | 中 | 高 | pin 一致性校验（version 精确相等）+ rebase 窗口全门禁重跑 | 开放 |
| R13 | 巨型生成文件拖垮 IDE / 构建 | INFRA | 低 | 高 | 无硬门禁；CI 编译时长趋势（11-progress.md） | 开放 |
| R14 | fourslash 77 万行测试体量的转换与运行时长 | INFRA | 中 | 高 | 阶段 8 fourslash 分级门禁 + CI 时长预算 | 开放 |
| R15 | GC vs arena：大项目编译吞吐与内存峰值 | PERF | 中 | 中 | 阶段 7 AOT 性能门禁（≤2× tsgo）+ 阶段 10 内存剖析 | 开放 |
| R16 | AOT 二进制体积膨胀 | PERF | 低 | 中 | CI 发布体积趋势记录；无硬门禁 | 开放 |
| R17 | 泛型 + 值类型实例化爆炸（ILC 体积/时长） | PERF | 低 | 低 | 阶段 0 起 publish AOT 门禁 + ILC 编译时长趋势 | 开放 |
| R18 | LSP 请求保序契约（同步前奏/异步尾部）移植错误 → 竞态 | SEM | 高 | 中 | 阶段 8 LSP stdio 回放集成测试 + fourslash 重复运行 | 开放 |
| R19 | api 层句柄身份：活对象注册表与 GC 回收 | SEM | 中 | 低 | 阶段 9 api 往返测试 + session 状态测试移植 | 开放 |
| R20 | 性能不达「≤2× tsgo」目标 | PERF | 高 | 中 | 阶段 7 AOT 性能硬门禁 + 阶段 5 起微基准早预警 | 开放 |
| R21 | testdata 283MB 与子模块体积的仓库管理 | INFRA | 低 | 高 | CI clone/checkout 时长监控；无硬门禁 | 开放 |
| R22 | 稳定排序：Go 稳定排序 vs C# `Array.Sort` 不稳定 | SEM | 中 | 高 | 各阶段差分夹具与基线门禁 + 重复运行探针 | 开放 |
| R23 | locale 匹配器：x/text Matcher vs 自研匹配的行为对齐 | SEM | 低 | 中 | 诊断本地化表驱动对照单测 | 开放 |

阅读优先级建议：动手写代码前先读 R01/R03/R04/R09/R22（每天都会踩）；进入对应阶段前读该阶段挂钩的条目（见各条「检测手段」）。

---

## 2. 风险详情

### R01 — 栈溢出：.NET 固定栈 vs Go 增长栈

- **类别** SEM ｜ **影响** 高 ｜ **概率** 高 ｜ **状态** 开放

**描述**。Go 的 goroutine 栈从 ~8KB 起按需增长（上限默认 1GB），tsgo 因此可以放任 parser/binder/checker 深度递归——`checkExpression`/`getTypeOfSymbol`/关系比较等函数群深度互递归，conformance 语料中存在专门的深递归压力样例（`testdata/tests/cases/compiler/deeplyNestedArrayTypes.ts`、`deeplyNestedTupleTypes.ts`、`excessivelyDeepConditionalTypes.ts` 等）。.NET 线程栈在创建时**固定**（主线程与线程池线程通常 1–8MB），且 `StackOverflowException` 不可捕获——直接 fail-fast 杀进程。这是主计划 §一.5 明示的「0 号风险」：不做处理，checker 阶段必然批量炸进程。原版 tsc.js 同样面临 V8 固定栈，靠 `--stack-size` 缓解；我们必须有等价机制。

**影响细节**。崩溃点不确定（取决于输入嵌套深度与调用链形状），最恶劣形态是「大部分语料通过、个别文件杀掉整个测试进程」，导致 conformance 跑批器无法收敛。

**缓解措施**：

1. **LargeStackExecutor（第 0 天落地，阶段 0 交付物）**：所有 parse/bind/check 入口一律跑在 64MB 大栈专用线程上（`Ts.Core.LargeStackExecutor`，`new Thread(entry, maxStackSize: 64 << 20)`）；WorkGroup 的并行/串行两个实现共用同一执行器（[01-architecture.md] 线程模型一节）。
2. **禁止栈逃逸**：`async/await` 的 continuation 会切换到线程池线程（默认 1MB 栈）。规则：编译器核心（Ts.Compiler 全部命名空间）**禁用 async**——tsgo 的 checker/parser 本来就是同步代码，直译不会引入 async；LSP 异步尾部（见 R18）若需进入 checker，必须经 LargeStackExecutor 重新投递。此规则写入 [05-conventions.md]，以代码评审 checklist 强制。
3. 不引入人工递归深度限制或手工栈化改写——那会改变与 tsgo 的诊断行为（tsgo 自身的 instantiation depth 限制是**逻辑**限制，必须直译保留；栈物理深度问题只用大栈解决）。
4. 测试宿主同样受约束：xUnit 测试方法体内直接调 checker 也会在测试线程（默认栈）上跑。测试基架统一提供「在大栈线程上执行」的包装（[08-testing-strategy.md]），conformance 跑批器全部经由它。

**检测手段**：阶段 3 门禁「全语料零崩溃解析」首次覆盖 parser 深度；阶段 5 conformance 全量跑批覆盖 checker 深度（deeplyNested*/excessivelyDeep* 样例天然是探针）；阶段门禁在 AOT 二进制上复跑（JIT/AOT 栈帧大小不同，AOT 帧通常更省，但仍需双轨验证，主计划 §四.2）。崩溃即 CI 红——该风险的检测是「免费」的，风险在于不装 LargeStackExecutor 就开始移植，返工面巨大。

---

### R02 — Go int（64 位）→ C# int（32 位）溢出

- **类别** SEM ｜ **影响** 中 ｜ **概率** 中 ｜ **状态** 开放

**描述**。Go 的 `int` 在 amd64/arm64 上是 64 位；C# `int` 恒为 32 位。直译时把 `int` 一律译为 C# `int` 在绝大多数场景安全（源文件尺寸、位置、长度均 < 2^31），但存在三类危险点：

1. **以 `math.MaxInt` 为饱和哨兵的算法**。实证：`internal/checker/checker.go:26530-26545` `getCrossProductUnionSize` 用 `size > math.MaxInt/n` 防乘法回绕、溢出时返回 `math.MaxInt`（Go 下 = 2^63−1）。C# 若译成 `int.MaxValue`（2^31−1），饱和阈值改变——本例中饱和值只要仍大于下游的超限报错阈值，行为即等价，但**每个这样的点都必须逐个论证**，不能默认等价。同类哨兵还散布在 `internal/checker/checker.go:9686,9689,9848,9858`、`internal/ls/lsutil/organizeimports.go:538-540,622`、`internal/vfs/vfsmatch/vfsmatch.go:29`（`UnlimitedDepth = math.MaxInt`）、`internal/scanner/regexp.go:418`（正则十进制转义 `Atoi` 溢出→`math.MaxInt` 哨兵——注意 Go 的 `Atoi` 在 64 位平台上 2^31~2^63 之间的值**不**溢出，C# `int.Parse` 会，故此点接受面不同，须直译为「溢出→哨兵」而非照抄 Parse）。
2. **溢出回绕语义**。Go 有符号整型溢出定义为二补码回绕；C# 默认 `unchecked` 上下文同样回绕——语义一致，但**项目内禁止全局开启 `CheckForOverflowUnderflow`**，且哈希混合、`pos*31+kind` 类代码不得加 `checked`。此规则写入 [05-conventions.md] 与 [07-aot-constraints.md] 的 MSBuild 属性清单。
3. **ES 语义数值转换**。`jsnum` 的 `ToInt32`/`ToUint32`（ECMA-262 模运算语义）依赖精确的位截断，Go/C# 都能表达，但实现差错难以人眼发现。

**缓解措施**：

1. [05-conventions.md] 的「整型宽度逐案表」为最高裁决：默认 `int`→`int`；凡出现 `math.MaxInt/MinInt`、`>> 32`/`<< 32`、`int(uint64)` 截断、长度乘法、哈希混合的移植点，必须在 PR 中标注宽度决策（保持 `int` 并论证，或改 `long`）。
2. `math.MaxInt` 哨兵点全库 grep 建清单（上文 8 处为现状快照），纳入对应模块移植 checklist（[06-module-inventory.md] 危险点列）。
3. `Atoi`→`int.Parse` 不是等价映射：Go `strconv.Atoi` 返回 64 位 int；逐点确认调用处对超 2^31 输入的行为（见上第 1 条 scanner/regexp.go 例）。

**检测手段**：阶段 1 jsnum 门禁（100 万随机 double 的 ToInt32/ToUint32/解析逐位相等）直接覆盖第 3 类；第 1、2 类靠各阶段差分夹具（溢出行为差异最终表现为诊断/类型串 diff）+ 移植评审规则。无单一 analyzer 能自动捕获，属「评审规则 + 夹具兜底」型风险。

---

### R03 — map 迭代序依赖（双向陷阱）

- **类别** SEM ｜ **影响** 高 ｜ **概率** 高 ｜ **状态** 开放

**描述**。这是一个**双向**风险：

- **正向（Go→C# 隐藏依赖）**：Go map 迭代序每次运行随机，因此 tsgo 里凡结果与序相关处都已显式排序或改用 `collections.OrderedMap`。C# `Dictionary<K,V>` 在只增不删时**貌似**呈插入序——移植者若漏掉 Go 侧的显式排序，或把 `OrderedMap` 偷懒换成 `Dictionary`，测试可能照样全绿（插入序恰好正确），直到某次 rehash、删除或无关重构改变枚举序才爆发非确定性 diff。**Dictionary 的枚举序是实现细节，绝不可作为契约。**
- **反向（tsgo 自身的补序不可省略）**：tsgo 为对齐 Strada（依赖 JS Map 插入序）加过显式排序。实证：`internal/modulespecifiers/util.go:30-40` 注释原文说明 “Strada relies on Map insertion order to break remaining ties deterministically; Go maps are unordered, so compare paths to keep the ordering stable”，并补了 `tspath.ComparePaths` 决胜比较。移植者看到 C# Dictionary「本来就有序」而删掉这类排序 = 语义漂移（且与上游未来行为分叉）。

**影响细节**。序依赖 bug 的特征是**间歇性、跨机器不可复现**，一旦混入 checker 缓存或诊断排序，conformance diff 会呈现「偶发翻转」，排查成本极高；这正是主计划把它列入 §一.6 承重规则的原因。

**缓解措施**：

1. **迭代序审计规则**（主计划 §一.6，细则在 [05-conventions.md] §7.3）：每个 `for k, v := range m` 的移植点必须二选一显式标注——`// map-iter: 序无关`（论证消费方与序无关），或补上/保留 Go 同款排序并标注 `// map-iter: 已排序`（标注拼写以 [05-conventions.md] §7.3 原文为准）。无标注不过评审。
2. Go 侧 `collections.OrderedMap`/`CopyOnWriteMap` 原样移植为保序容器（主计划 §一.6），**不得**用裸 `Dictionary` 顶替。
3. 建议性工程手段：Debug 构建提供 `ShuffledDictionary` 包装（枚举时随机化顺序）用于测试基架内部的抽查开关，模拟 Go 的随机序以暴露隐藏依赖。此为基架内部工具，不进入产品程序集（避免与直译原则冲突；具体设计归 [08-testing-strategy.md]）。
4. 涉及并发填充的 map（checker 关系缓存 per-checker，见 [03-checker-design.md]），坚持主计划的「类型不跨 checker 共享」不变量，避免枚举序叠加并发时序。

**检测手段**：阶段 5 终检「4-checker 并行输出 ≡ 单 checker 输出」是最强探针（并行改变填充时序→枚举序变化→序依赖立即显形）；主计划 §四.3 确定性检验（串行/并行全等）贯穿所有阶段；差分夹具重复运行（同一输入跑 N 次 diff 自身）作为廉价日常探针。

---

### R04 — nil slice/map 读语义差 → C# NRE

- **类别** SEM ｜ **影响** 中 ｜ **概率** 高 ｜ **状态** 开放

**描述**。Go 对 nil 集合的读操作全部良性：`len(nil slice)==0`、`for range nil` 零次迭代、`nilMap[k]` 返回零值、`append(nil, x)` 分配新底层数组。C# 对应操作（`null.Length`、`foreach (var x in null)`、`nullDict[k]`）全部抛 `NullReferenceException`。tsgo 大量利用该语义（未初始化字段直接 range/len），逐函数直译时每一处都是潜在 NRE。

另有**反向承重点**：nil 与空切片在 Go 中可区分，且该区分在 JSON 写出时承重——`omitzero` 语义下 nil 切片省略字段、空切片写 `[]`（主计划 §一.5 .tsbuildinfo 字节保真）。若 C# 侧用「空集合代替 null」的防御性初始化一刀切，会破坏 .tsbuildinfo 字节保真（该省略的字段被写出）。

**缓解措施**：

1. [05-conventions.md] 切片/映射逐案表为准：保留 null 语义（字段默认 null，不做防御性初始化），读点用约定助手（如 `len(s)`→静态助手对 null 返 0、`range`→null 容忍的枚举助手或显式 `?? []` 按逐案表选择）——具体助手命名与选型以 [05-conventions.md] 为准，本册只登记风险。
2. `Nullable enable` 全库开启（Directory.Build.props，已就位）：流分析在编译期把大多数「可能为 null 的解引用」变成警告；配合 `WarningsAsErrors` 提升为硬失败（[07-aot-constraints.md]）。
3. nil/空区分承重点（JSON 写出、`SameMap`/`Same*` 助手的恒等返回语义）在 [05-conventions.md] 中单列，禁止一刀切初始化。

**检测手段**：NRE 是崩溃型缺陷——阶段 3「全语料零崩溃解析」与其后所有全量跑批天然捕获；Nullable analyzer 在编译期拦截大部分；omitzero 破坏由阶段 7 .tsbuildinfo 字节 diff 捕获。

---

### R05 — 浮点→字符串：ES6 版式与最短往返

- **类别** SEM ｜ **影响** 高 ｜ **概率** 中 ｜ **状态** 开放

**描述**。数字→字符串的输出逐字进入 `.types` 基线（数字字面量类型显示）、const enum 内联值、printer 的 JS 输出——任何一位差异都是字节 diff。tsgo 的实现在 `internal/jsnum/string.go:17-38`：`Number.String()` 处理 NaN/±Infinity、安全整数快路径（`strconv.FormatInt`），其余走内部 json 包的最短往返格式化（jsnum 包内含 ryu 参考测试 `ryu_test.go` 钉死最短数字位）。ECMA-262 `Number::toString` 版式规则与 .NET 默认版式**不同**：

- .NET `double.ToString()` 自 Core 3.0 起已是**最短往返数字位**（这部分是安全的，主计划已核验）；
- 但版式层差异巨大：.NET 输出 `1E+16` 类大写 `E` 且指数切换阈值与 JS 不同——JS 在 ≥1e21 才用指数记法（`1e20` → `"100000000000000000000"`，`1e21` → `"1e+21"`），<1e-6 用负指数；`e+`/`e-` 后**不补零**；`-0` → `"0"`；这些必须自建 ES6 版式层，仅复用 .NET 的最短数字位提取。

**缓解措施**：

1. 阶段 1 按主计划 §二 表实现 jsnum：在 .NET 最短往返之上加 ES6 版式层（安全整数快路径、≥1e21 与 <1e-6 指数切换、`e+`/`e-` 不补零、`-0`→`"0"`、Infinity/NaN 字面）。
2. 移植 `internal/jsnum/string_test.go` 全部表驱动用例（`{1e21,"1e+21"}`、`{1e20,"1000…000"}` 等）钉死边界。
3. 数字**解析**方向（`StringToNumber`，string.go:40 起）同样承重（数字字面量求值），一并纳入门禁；十六进制/八进制/二进制/分隔符路径逐一对拍。
4. 禁止在任何承重路径调用 `double.ToString()` 裸格式（[07-aot-constraints.md] 禁用 API 清单 + 评审）；所有数字格式化必须经 `Ts.JsNum`。

**检测手段**：阶段 1 jsnum 专项门禁——100 万随机 double 的「数字→字符串 / ToInt32 / ToUint32 / Number 解析」与 Go 夹具逐位相等（夹具由 dumptool 侧生成）；阶段 5 `.types` 基线与阶段 6 `.js` 输出做全量兜底。

---

### R06 — xxh3 必须哈希 UTF-8 字节（.tsbuildinfo 承重）

- **类别** SEM ｜ **影响** 高 ｜ **概率** 中 ｜ **状态** 开放

**描述**。主计划 §一.6 已核验分级：checker 缓存键与项目层内容哈希是纯内存用途（只需自洽）；但 **incremental 的 `ComputeHash`（`internal/execute/incremental/snapshot.go:32`）承重**——其 XXH3-128 小写 hex 输出持久化进 `.tsbuildinfo` 的 `version`/`signature` 字段，并逐字出现在 tsbuild 基线中。C# 字符串是 UTF-16：若实现者把 `string` 直接喂给哈希（`MemoryMarshal.AsBytes(str.AsSpan())` 或逐 char 累加），得到的是 UTF-16LE 字节序列的摘要，与 Go 对 UTF-8 字节的摘要**完全不同**——增量构建基线全线崩溃，且产物与 tsgo 的 .tsbuildinfo 互不认账（signature 不匹配 → 增量失效或误判）。这类错误在单元测试里若用同样错误的参考值会自洽通过，隐蔽性强。

**缓解措施**：

1. `Ts` 侧 `ComputeHash` 的公开签名只接受 `ReadOnlySpan<byte>`；接受 `string` 的重载内部**必须**先 `Encoding.UTF8.GetBytes`（或流式 `Encoder` 到 `XxHash128.Append`），并在 XML doc 注明「Go 对齐：哈希 UTF-8 字节」。
2. 依赖映射固定为 `System.IO.Hashing.XxHash128`（同为 XXH3-128，主计划 §一.5）；输出转换固定为「128 位大端规范 16 字节 → 小写 hex」。
3. 固定测试向量入库（主计划 §一.6）：`xxh3_128("export const a = 10;") = 3a1e9965f8602302ee6ae53756eac8be`——该向量同时钉死字节序与 hex 大小写；另补含非 ASCII 字符的向量（从 Go 侧现跑生成）以显式区分 UTF-8/UTF-16 错误。
4. tracing 用 XXH3-64 → `XxHash3`（非承重，无向量要求）。

**检测手段**：固定向量单测（阶段 1，含非 ASCII 向量专杀 UTF-16 错误）；阶段 7 门禁「.tsbuildinfo 与 tsgo 字节相等」「增量=全量输出」全量兜底。

---

### R07 — JSON 转义与键序的字节保真

- **类别** SEM ｜ **影响** 高 ｜ **概率** 高 ｜ **状态** 开放

**描述**。`.tsbuildinfo` 与部分基线是**字节级契约**（主计划 §一.5，已核验 Go 输出形态）。System.Text.Json 的默认行为与 Go `encoding/json`（及 tsgo 的定制写出）存在多处系统性差异，任何一处即字节 diff：

1. **转义**：STJ 默认 `JavaScriptEncoder` 转义 `<`、`>`、`&`、`'` 与全部非 ASCII（`\uXXXX`）；Go 侧非 ASCII 直通、控制字符用短转义（`\n`、`\t`）。必须用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 且核对其与 Go 的逐字符转义表完全一致（残余差异逐字符对拍）。
2. **键序**：契约为「Go struct 声明序」（BuildInfo 19 字段序已记录于主计划）。反射序列化不保证声明序且 AOT 禁用；STJ 源生成的属性序也不是可依赖契约。
3. **omitzero**：省略 false/0/""/nil 的语义须逐字段手工表达（与 R04 的 nil/空区分交叉）。
4. **多态形状**：7 种（`BuildInfoRoot` 为 `number|[start,end]|string` 等，`internal/execute/incremental/buildInfo.go:34-448`）。
5. **版式**：单行紧凑、无尾换行；无效 UTF-8 → U+FFFD；buildinfo 只含整数、无浮点写出（主计划已核验，浮点风险隔离在 R05）。

**缓解措施**：

1. 架构决策已定（主计划 §一.5，细化在 [01-architecture.md]）：**不走对象序列化**，用手写 `Utf8JsonWriter` 写出器，字段顺序与 omitzero 条件逐字段硬编码，7 种多态形状手写分支。
2. 测试基架配套 readable-baseline 写出器（2 空格缩进 + `size=` 紧凑文本字节长），归 [08-testing-strategy.md]。
3. `version` 字段 = `core.Version()`（`"7.1.0-dev"`）加载时精确字符串相等校验——同时是 R12 的 pin 探针。
4. [07-aot-constraints.md] 政策：反射序列化器封禁、STJ 仅源生成上下文（LSP 线缆层）；.tsbuildinfo 路径连源生成都不用，纯 writer。

**检测手段**：阶段 7 「.tsbuildinfo 与 tsgo 字节相等」硬门禁；tsbuild 基线（含 readable 形态）diff；转义表逐字符单测（0x00–0x1F、`<>&'`、代理对、无效序列）。

---

### R08 — Go regexp（RE2，线性时间）vs .NET Regex（回溯）

- **类别** SEM ｜ **影响** 中 ｜ **概率** 中 ｜ **状态** 开放

**描述**。Go `regexp` 是 RE2 家族：**保证线性时间**、不支持反向引用与环视；.NET `Regex` 默认回溯引擎：功能超集但最坏指数时间（ReDoS 面），另有 `RegexOptions.NonBacktracking`（.NET 7+）提供线性时间引擎、构造期拒绝反向引用/环视——与 RE2 行为面最接近。已对 `/Users/token/Desktop/code/typescript-go-main` 生产代码完成全量盘点（`grep -rn 'regexp\.(MustCompile|Compile|...)' internal cmd`，排除 `_test.go`），使用点分四类：

**A. 产品运行时（进入 AOT 二进制，逐点评估）**：

| # | 位置 | 模式 | 用途 | 评估与映射 |
|---|---|---|---|---|
| A1 | `internal/diagnostics/diagnostics.go:115` | `{(\d+)}` | 诊断消息占位符替换（`Format`，diagnostics.go:117-135）——**errors.txt 承重** | 模式平凡、无回溯放大结构。映射：`[GeneratedRegex]`（Roslyn 源生成，AOT 安全）或手写扫描。注意 Format 还做无效 UTF-8→U+FFFD 参数清洗（diagnostics.go:122-124），一并直译 |
| A2 | `internal/stringutil/util.go:243` | `\\.` | `UnquoteString` 反转义（util.go:254） | 平凡；建议手写单遍扫描（语义 = 删除反斜杠保留后随字符） |
| A3 | `internal/semver/version.go:19,27,28,36,37,42`；`version_range.go:14,15,28,33,41` | 全锚定 `(?i)^…$` 系列 | semver 解析——生产消费方：模块解析器 typesVersions（`internal/module/util.go:9,13,21`，**阶段 4**）、`internal/packagejson/cache.go`、ATA（`internal/project/ata/ata.go`、`discovertypings.go`，阶段 8） | 全部锚定、无嵌套量词，回溯引擎下也线性；纯 ASCII 类，`(?i)` → `RegexOptions.IgnoreCase \| CultureInvariant` 等价。映射：`[GeneratedRegex]`。语义陷阱：Go 捕获组未参与匹配返回空串位置约定 → C# `Group.Success` 判定，逐组核对 |
| A4 | `internal/modulespecifiers/util.go:59-121` `stringToRegex` + `regexp.Compile`（util.go:115） | **用户提供的动态 pattern**（LSP 偏好 `AutoImportSpecifierExcludeRegexes`；消费点 `specifiers.go:54,422,444,570-571`、`internal/ls/autoimport/specifiers.go:16,35`、`internal/ls/lsutil/userpreferences.go:879`） | **本条目核心风险点**。Go：RE2 编译失败（含反向引用/环视）→ 缓存 nil、静默跳过该 pattern（util.go:115-119）；线性时间保证用户 pattern 无 ReDoS。映射：`RegexOptions.NonBacktracking`——同为线性时间、同在构造期拒绝反向引用/环视；构造抛异常 → 缓存 null 跳过，对齐 Go。**残余偏差（接受并记录）**：RE2 与 NonBacktracking 的特性面不完全同构（字符类细节、`\b` Unicode 语义等边角），奇异用户 pattern 的接受/拒绝面可能不同——非基线承重（只影响 auto-import 排除偏好），列为已知偏差。>1000 条清空的 pattern 缓存（util.go:103-105）与 `/pattern/flags` 拆解、仅识别 `i` 标志的逻辑（util.go:59-85）原样直译 |
| A5 | `internal/lsp/stack_sanitizer.go:17` | `(?i)(key\|token\|signature\|sig\|pwd)([(\[.\|])` | LSP panic 堆栈上报前防 VS Code 遥测误脱敏 | 模式平凡。但整个 `sanitizeStackTrace` 针对 **Go 运行时栈格式**（stack_sanitizer.go:24 起搜 `runtime/debug.Stack()`）——C# 侧须按 .NET `Environment.StackTrace` 格式重写解析逻辑，属于本模块少数「不可直译、须适配」的点，登记入 [06-module-inventory.md] 危险点列 |

**B. 生成时（不进产物）**：`internal/bundled/generate.go:132,134` 与 `internal/diagnostics/generate.go:403-405`——两文件均 `//go:build ignore`（各自首行），职责已由 `tools/generator/` 的 TS 脚本取代（[04-codegen.md]），无 .NET Regex 问题。

**C. 测试基架（测试项目不 AOT，可用 .NET Regex，但语义仍须核对）**：`internal/testrunner/test_case_parser.go:18,41,44`（`(?m)` 多行选项拆解——.NET `RegexOptions.Multiline` 的 `^/$` 行为与 Go `(?m)` 对齐，`$` 均在 `\n` 前匹配，pattern 自带 `[^\r\n]*` 兜住 `\r`）；`internal/testrunner/compiler_runner.go:28,30`；`internal/fourslash/baselineutil.go:129,172,285,296,341,342,354,457,458,914`；`internal/testutil/baseline/baseline.go:184`；`internal/testutil/tsbaseline/util.go:11-15`（其中 `nonWhitespace = \S`（util.go:12）**与 R09 交叉**：Go `\S` 逐 rune 匹配，.NET `\S` 逐 UTF-16 码元匹配，星面字符处理为 2 个码元——error_baseline 的空白垫必须按码点语义实现，不能照搬 .NET Regex 替换）；`internal/testutil/tsbaseline/error_baseline.go:31,32`；`internal/testutil/tsbaseline/type_symbol_baseline.go:25-27`（`[\r  ]|\r?\n` → .NET 直译，注意交替顺序保留）；`internal/testutil/fsbaselineutil/differ.go:97`（`\x{FFFD}` → .NET 写法 `�`）；`internal/testutil/harnessutil/harnessutil.go:959-971`（`EnumerateFiles` 接受调用方传入的 regexp）。

**D. 确认为手写、非 regexp 的实现（禁止用 .NET Regex「省事」替代）**：

1. **scanner 的正则字面量校验器是手写递归下降解析器**：`internal/scanner/regexp.go`（1,076 行，`regExpParser`），由 `scanner.go:1196-1210` 在 `shouldReportErrors` 时构造并 `parser.run()`；flags 校验/annexB/unicodeSetsMode/命名捕获组检测（scanner.go:1104-1216）均为手写。**必须逐行直译**——它产出的诊断逐字进入 errors.txt 基线，任何「换成 .NET Regex 试编译」的偷懒实现都会导致诊断集合完全不同。
2. **vfsmatch glob 匹配器无 regexp**：`internal/vfs/vfsmatch/vfsmatch.go`（含 `MATCHING_ALGORITHM.md` 算法文档）是手写 include/exclude 匹配（Strada 是 glob→regex，tsgo 重写为直接匹配且性能承重）——直译手写实现。

**缓解措施**：

1. [07-aot-constraints.md] 政策固化：产品程序集内静态 pattern 一律 `[GeneratedRegex]`（源生成，AOT 安全、无反射）；**一切非编译期字面量的 pattern 一律 `RegexOptions.NonBacktracking`**（消灭用户输入 ReDoS 面 + 对齐 RE2 接受面）；默认回溯引擎在产品代码中封禁（analyzer/评审 checklist）。
2. A 类 5 点按上表逐点直译；D 类 2 点在 [06-module-inventory.md] 标注「手写实现，禁 Regex 替代」。
3. C 类基架允许 .NET Regex，但 `\S` 类逐码元语义点须以码点语义重写（与 R09 垫片合并处理）。

**检测手段**：A3 → 阶段 4 模块解析 trace 夹具；A1 → 阶段 3/5 诊断与 errors.txt 门禁；A4/A5 → 阶段 8 fourslash（autoImport 排除偏好用例）与 LSP 回放；D1 → 阶段 2/3 scanner/parser 门禁 + 阶段 5 errors.txt（正则诊断样例密集）；D2 → 阶段 4 tsoptions/文件枚举基线。政策合规靠 [07-aot-constraints.md] 的 CI analyzer 门禁与评审。

---

### R09 — errors.txt 码点垫片与 sourcemap UTF-8 字节长垫片

- **类别** SEM ｜ **影响** 中 ｜ **概率** 高 ｜ **状态** 开放

**描述**。主计划的 UTF-16 决策（删除 PositionMap，一切位置即 UTF-16 码元）经对抗验证为 PARTIAL：所有基线**行列数字**均为 UTF-16，安全；但测试基架有两处刻意保留的 Go 语义，移植时必须打「垫片」而非顺手改成 UTF-16：

1. **errors.txt 波浪线按 Unicode 码点计数**。实证：`internal/testutil/tsbaseline/error_baseline.go:211` 用 `nonWhitespace.ReplaceAllString(line[:squiggleStart], " ")`（Go `\S` 逐 rune）铺空白垫、`error_baseline.go:214` 用 `utf8.RuneCountInString(line[squiggleStart:squiggleEnd])` 计算波浪线数量——星面字符 = 1 空格/1 波浪线。C# 若按 `string.Length`（UTF-16 码元）计数，星面字符会产出 2 个，至少 3 个含星面字符的基线文件 diff（判别样例：`regularExpressionWithNonBMPFlags`）。垫片：对 UTF-16 切片按**码点**迭代计数（代理对算 1）。
2. **.sourcemap.txt 续行标记终列 = JS 行的 UTF-8 字节长 − 1**。实证：`internal/testutil/harnessutil/sourcemap_recorder.go:350-353`——`len(jsFileText)` 是 Go 字节长，`endColumn = len(jsFileText)-1`。C# 垫片：`Encoding.UTF8.GetByteCount(jsLineText) - 1`。
3. **休眠路径备案**：`internal/testutil/tsbaseline/type_symbol_baseline.go:236-237` 的 underline 前置 pad 用 `len(lineText)`（字节长）循环写空格——当前 `result.underline` 恒为空、路径休眠未启用。**处置：登记不修**；直译时按 Go 字节语义注释存档，若上游启用该路径（R12 rebase 窗口审阅项），需按字节长实现 pad 并补基线对拍。

**双向危险**：忘打垫片 → 上述基线 diff；打过头（把编译器本体的位置/诊断长度也改成码点/字节）→ 大面积 diff。垫片的作用域**仅限测试基架**（tests/Ts.TestUtil 内的 baseline 写出器），编译器本体一律 UTF-16。

**缓解措施**：

1. 两处垫片在基架代码中以显式注释标注（建议统一标记 `// SHIM(go-rune-semantics)` / `// SHIM(go-byte-length)`，规范归 [05-conventions.md]），可 grep 审计。
2. [08-testing-strategy.md] 的 baseline 写出器规格中把这两点列为规格条款（非实现细节）。
3. 含星面字符的基线样例（`regularExpressionWithNonBMPFlags` 等 ≥3 个文件）纳入阶段 5 早期冒烟子集，不要等全量跑批才发现。

**检测手段**：阶段 5 errors.txt 100% 字节匹配（垫片 1）；阶段 6 `.sourcemap.txt` 基线（垫片 2）；休眠路径无检测手段（登记 + rebase 窗口人工审阅）。

---

### R10 — InvariantGlobalization 与 JS 精确大小写

- **类别** SEM ｜ **影响** 高 ｜ **概率** 中 ｜ **状态** 开放

**描述**。Directory.Build.props 已定 `InvariantGlobalization=true`（AOT 体积与确定性）。这带来两层大小写语义问题：

1. **JS 精确大小写是承重语义，且 tsgo 有专门实现**：`internal/stringutil/js_case.go:9-67` 的 `ToLowerJS`/`ToUpperJS` 实现了 ECMA-262 `String.prototype.toLowerCase/toUpperCase` 的**全映射**——含 SpecialCasing 多字符映射、Final_Sigma 条件映射（js_case.go:28-31 的 casedBefore 流式追踪）、孤代理逐字保留（js_case.go:23-27）。消费方：checker 的 intrinsic string mapping `applyStringMapping`（`internal/checker/checker.go:28999-29013`，`Uppercase<T>`/`Lowercase<T>`/`Capitalize<T>`/`Uncapitalize<T>`）——结果逐字进入 `.types` 基线。.NET 的 `ToLowerInvariant/ToUpperInvariant` 在 invariant 模式下只做 Unicode **简单**单字符映射：不做 ß→SS、不做 Final_Sigma、多字符 SpecialCasing 全缺 → 用它实现 intrinsic 映射必产字节 diff。
2. **比较与排序**：`internal/stringutil/compare.go` 的 `EquateStringCaseInsensitive`（compare.go:9-13，`strings.EqualFold` = Unicode 简单折叠）与 `CompareStringsCaseInsensitive`（compare.go:34-55，逐 rune `unicode.ToLower`）语义是「Go 简单折叠」，既不是 .NET `OrdinalIgnoreCase`（其折叠表略有差异面）也不是文化相关比较。文件路径大小写折叠贯穿 tspath/vfs（与 R11 叠加），任何折叠面差异都会改变大小写不敏感文件系统上的路径命中。

**缓解措施**：

1. **逐行移植 `js_case.go` 连同其数据表**（specialCasingMappings、isUnicodeCaseIgnorable、isSigmaCased 等；表数据生成归 [04-codegen.md] Unicode 表族）；禁止用 .NET 内置大小写 API 实现 intrinsic 映射。
2. 比较类：所有字符串比较显式 `StringComparison.Ordinal`；大小写不敏感路径**直译 Go 的逐 rune 折叠逻辑**而非替换为 `OrdinalIgnoreCase`（差异面虽小，但路径命中是承重语义；[05-conventions.md] ordinal 审计规则）。
3. [07-aot-constraints.md] 打开全部文化相关 analyzer（CA1304/CA1305/CA1307/CA1309/CA1310）为 error，`ICU` 缺失下的隐式文化调用在编译期拦截。
4. 单测：从 Go 侧生成大小写映射差分夹具（全 BMP 码点 + 代表性星面/条件映射样例）对拍 `ToLowerJS/ToUpperJS/EqualFold` 三件套。

**检测手段**：CA13xx analyzer（编译期）；大小写映射差分夹具（阶段 1，stringutil 归属阶段 1 地基）；阶段 5 `.types` 基线中 intrinsic string mapping 样例；阶段 8 fourslash（补全排序对大小写折叠敏感）。

---

### R11 — Windows 路径与大小写敏感性探测

- **类别** SEM ｜ **影响** 中 ｜ **概率** 中 ｜ **状态** 开放

**描述**。文件系统语义差异集中三点：

1. **大小写敏感性探测**：`internal/vfs/osvfs/os.go:47-75`——Windows 直接判不敏感；wasm 判敏感；其余平台用「把可执行文件路径逐字符换大小写后 `os.Stat`」探测（swapCase，os.go:78-88），探测 IO 失败直接 panic；结果在**进程启动时**以 Go 包级 var 一次性固化（os.go:47 注释：尽早探测避免可执行文件被移动）。C# 直译点：`os.Executable()`→`Environment.ProcessPath`；包级 var 初始化→`static readonly`（模块初始化时机在 AOT 下等价但须确认首次触达点早于任何 VFS 使用）；swapCase 的逐 rune 换写与 R10 的折叠实现共用直译逻辑。
2. **Windows 路径形态**：盘符、UNC、`\`/`/` 归一化全部在 `internal/tspath`（1.4k 行，阶段 1）——纯字符串算法可直译，但开发主力在 macOS（大小写不敏感 HFS+/APFS）上，Windows 特有分支（盘符大小写归一、UNC root 长度）易欠测试。
3. **大小写不敏感文件系统上的行为**：conformance 中存在大小写重复文件名样例（`internal/testutil/tsbaseline/error_baseline.go:226-231` 专门处理 case-dupe 的错误计数），本机文件系统敏感性会影响测试基架行为——必须以 vfs 抽象隔离（测试用内存 vfs 指定敏感性，不依赖宿主）。

**缓解措施**：

1. 直译探测逻辑与 panic 行为（探测失败 = 环境损坏，快速失败正确）。
2. `internal/tspath` 与 `internal/vfs` 的 Go 单测**全量移植**（阶段 1 门禁「移植的 Go 单测全绿」）。
3. CI 三平台矩阵（win/linux/mac）从阶段 0 起对每个门禁全跑（主计划阶段 0/10 均要求三平台）；Windows 上不只跑单测，conformance 跑批同样全量。
4. 测试基架一律走内存 vfs 显式指定 `UseCaseSensitiveFileNames`，宿主真实文件系统只在 osvfs 自身的测试中触达（对照 `internal/vfs/osvfs/os_test.go:54-64`）。

**检测手段**：阶段 1 tspath/vfs 移植测试；三平台 CI（任何平台特有分歧在门禁复跑中显形）；阶段 7 在 Windows 上以 AOT 二进制编译真实项目的门禁。

---

### R12 — 上游漂移（tsgo 持续开发）

- **类别** PROC ｜ **影响** 中 ｜ **概率** 高 ｜ **状态** 开放

**描述**。tsgo 是微软活跃开发中的仓库；移植周期以月计（阶段 5 独占 33%），期间上游会持续推进：checker 行为修复→基线内容变化、ast.json/diagnosticMessages.json 变化→生成器输入漂移、新增语言特性→模块清单失真。若追踪活动头，「与 tsgo 逐字节 diff」的验收货币本身会晃动——同一天内两次跑批可能因为上游 commit 而结果不同，门禁失去意义。

**缓解措施**：

1. **pin**：`/Users/token/Desktop/code/typescript-go-main` 快照与 `_submodules/TypeScript` 钉扎到同一对应 commit（钉扎策略与升级节奏归 [00-overview.md]）；testdata 基线、ast.json 副本、diagnosticMessages.json 均以 pin 为准。
2. **rebase 窗口**：升级只在计划的窗口进行，动作原子化——bump 快照与 submodule → 重跑全部生成器（[04-codegen.md] 再生成命令）→ 同步 testdata 基线 → 全部已通过门禁重跑 → 审阅上游变更日志（重点：checker/emitter 行为变更、R09 休眠路径是否被启用、arena/并发结构变化）。窗口之间**绝不**部分同步。
3. **pin 一致性探针**：`version` 字段加载时与 `core.Version()`（`"7.1.0-dev"`，主计划 §一.5）精确字符串相等校验；CI 校验生成产物与生成器输入的哈希一致（防「改了输入忘了再生成」）。
4. 当前 pin 与历次 rebase 记录记入 [11-progress.md]。

**检测手段**：CI 的 pin/产物一致性校验（每次构建）；rebase 窗口的全门禁重跑（漂移影响被限制在窗口内、可归因）；[11-progress.md] 签核日志留痕。

---

### R13 — 巨型生成文件拖垮 IDE / 构建

- **类别** INFRA ｜ **影响** 低 ｜ **概率** 高 ｜ **状态** 开放

**描述**。生成面体量：AST 七件套 `.g.cs`（~190 节点 × 转换/访问器/工厂，预计数万行；[02-ast-design.md] §16，裁决 R2）、LSP 协议 17k 行、诊断表 2,154 条、Unicode 表；手写侧 checker.go 31,926 行对应的 partial class 群。单文件过大时 Roslyn 语义分析、IDE 打开/高亮、增量编译、git diff 审阅全部劣化。主计划已为此弃用 Roslyn 源生成器（内存生成物拖垮 IDE 是弃用理由之一，§一.1）。

**缓解措施**：

1. AST 产物拆 7 个 `.g.cs`（Nodes/NodeCasts/NodeFactory/Visitors/Accessors/SyntaxKind/GlobalUsings，[02-ast-design.md] §16 定稿，裁决 R2）；单产物若仍超 ~15k 行，在 [04-codegen.md] 中按 kind 分片再拆。
2. checker 拆 partial class（Checker.cs / Checker.Relater.cs / …），22 文件拆分表以 [03-checker-design.md] §4.2 为唯一权威（裁决 R1）。
3. 生成文件统一头部 `<auto-generated/>` + `[GeneratedCode]`：analyzer 跳过、编辑器只读提示。
4. `.gitattributes` 标 `*.g.cs linguist-generated=true`（diff 折叠）；评审规约「生成物 diff 只审生成器与输入的 diff」。

**检测手段**：无硬门禁（开发体验型风险）；CI 记录全量构建与增量构建时长趋势入 [11-progress.md]，回归超阈值时人工介入再拆分。

---

### R14 — fourslash 77 万行测试体量的转换与运行时长

- **类别** INFRA ｜ **影响** 中 ｜ **概率** 高 ｜ **状态** 开放

**描述**。实测 `internal/fourslash` 共 **781,234 行** Go，其中 `tests/` 下 **4,295 个**测试文件（289 顶层手写 + 210 tests/manual/ + 3,796 gen/，计数口径按评审裁决 R10 全文档统一）——而且这些 Go 文件本身就是**生成物**：tsgo 用 `internal/fourslash/_scripts/convertFourslash.mts` 从 Strada（`_submodules/TypeScript`）的 fourslash 用例转换生成，并用 `failingTests.txt`/`crashingTests.txt`/`manualTests.txt`/`unparsedTests.txt` 维护跳过/人工清单。风险有三：(a) 若把 77 万行当手工移植对象，工作量灾难；(b) 4,295 个文件的 C# 测试项目编译时长与 xUnit 发现开销可能把 CI 拖到不可用；(c) 运行时长——fourslash 每用例要起语言服务会话，全量串行可能数小时。

**缓解措施**：

1. **生成而非手翻**（与 tsgo 同构的做法，非新架构决策）：以 `convertFourslash.mts` 为蓝本改造出 C# 目标的转换器（放入 tools/generator/，产物入库；详细设计归 [08-testing-strategy.md] 与 [04-codegen.md]），跳过清单四件套原样继承并加「C# 侧特有跳过」段。
2. 编译体量：tests/Ts.Fourslash.Tests 按首字母/目录分片为若干程序集（xUnit v3 程序集级并行），控制单程序集 <20MB IL。
3. 运行时长：xUnit 并行 + CI 分片矩阵；PR 门禁只跑冒烟子集，阶段 8 分级门禁（50%→90%→99%+）与夜间任务跑全量。
4. 阶段 8 分级本身就是对体量风险的缓解：不要求一次性 100%。

**检测手段**：阶段 8 fourslash 分级门禁（通过率数字直接反映转换器质量）；CI 时长预算告警（超 60 分钟标红，阈值可在 [08-testing-strategy.md] 调整）；转换器产物计数对账（tsgo 4,295 个 = 289+210+3,796，vs C# 侧生成数 + 跳过清单差额 = 0）。

---

### R15 — GC vs arena：大项目编译吞吐与内存峰值

- **类别** PERF ｜ **影响** 中 ｜ **概率** 中 ｜ **状态** 开放

**描述**。tsgo 用手写 arena（`internal/core/arena.go`）批量分配 AST/checker 对象（使用方遍布 ast/binder/checker/parser/printer/sourcemap：如 `NodeFactory` 的 `nodeListArena`，`internal/ast/ast.go:129,160,168`；释放钩子 `ReleaseArenas`，ast.go:3053）。主计划决策删除全部 ~50 个 arena，`arena.New()`→`new T()`（§一.1）——论据是 .NET gen0 本身是 bump 分配器。该论据对**分配速度**成立，但 arena 还有两个 GC 拿不回来的好处：(a) 同代对象**紧凑共址**（遍历局部性）；(b) 整体丢弃、无标记成本。AST/类型图对象全程存活 → 必然晋升 gen2 → 大项目编译时 gen2 堆巨大：完整 GC 的标记成本与堆峰值都可能显著高于 Go。另外 C# 字符串是 UTF-16：源文本与字符串驻留内存约为 Go（UTF-8）的 ~2 倍，直接推高峰值并稀释缓存局部性（与 R20 叠加）。

**缓解措施**：

1. 结构性省内存已在设计中：节点删 data 指针省 16B/节点（主计划 §一.1）、Type 层坍缩 TypeData（§一.2）。
2. 阶段 10 GC 调优：ServerGC + 并发 GC、`GCHeapHardLimit`/`ConserveMemory` 试验矩阵；长存活大对象（诊断表、Unicode 表）走 RVA 静态数据零分配（主计划 §一.4）。
3. 剖析驱动而非猜测：`dotnet-counters`/`dotnet-gcdump` 对真实大项目（如 self-check 或大型 OSS 项目）采样，热点对象类字段紧凑化（枚举窄化为 byte/short 等——仅当不改可观察行为）。
4. 若剖析证明某热点确需池化，个案引入对象池并记录（不推翻「删 arena」的全局决策；个案须过 [03-checker-design.md] 的确定性论证）。

**检测手段**：阶段 7 AOT 门禁（真实大项目编译输出全等 + 性能 ≤2× tsgo，内存峰值同场采集）；阶段 10 BenchmarkDotNet 套件与内存剖析报告（[08-testing-strategy.md]，性能基准平时不设门禁、阶段 7/10 有硬指标）。

---

### R16 — AOT 二进制体积膨胀

- **类别** PERF ｜ **影响** 低 ｜ **概率** 中 ｜ **状态** 开放

**描述**。Native AOT 静态链接全部可达代码与数据：~30 万行直译代码 + 生成面（LSP 17k 行、诊断 2,154 条消息字符串、Unicode 表）+ 108 个 lib.d.ts 内嵌资源（实测 `internal/bundled/libs` = 3.9MB 文本）+ ILC 的类型元数据。预估产物几十 MB 量级（tsgo 原生二进制同为几十 MB，故非竞争劣势），风险在于失控增长：泛型实例化（R17）、无意拉入反射根、STJ 上下文膨胀可把体积推过 100MB+，影响分发与启动 mmap。

**缓解措施**：

1. 体积不是门禁指标（主计划未设），但从阶段 0 起 CI 记录三平台发布体积趋势入 [11-progress.md]，异常跳变（>10%/次）触发人工归因。
2. 常规瘦身项备选：`IlcOptimizationPreference=Size`（若性能门禁允许）、`StackTraceSupport=false` 评估（与 R08-A5 堆栈上报冲突，需权衡）、lib.d.ts 资源 gzip 内嵌 + 启动解压（候选项，若采纳落入 [07-aot-constraints.md] 内嵌资源政策）。
3. 反射封禁（[07-aot-constraints.md]）本身就是最大的体积护栏——无反射根 = ILC 可激进裁剪。

**检测手段**：CI 体积趋势记录（无硬门禁）；`dotnet publish` 的 ILC 输出与 `sizoscope`/mstat 分析在异常时使用。

---

### R17 — 泛型 + 值类型实例化爆炸

- **类别** PERF ｜ **影响** 低 ｜ **概率** 低 ｜ **状态** 开放

**描述**。AOT 下引用类型泛型共享一份代码，**值类型泛型每个实例化单独生成**。本项目结构键全部是 `readonly record struct`（主计划 §一.6），叠加 `Dictionary<K,V>`、`HashSet<K>`、比较器等容器族，每个键类型 × 每种容器 = 一份独立机器码。~30 个链接 struct + 各类缓存键预计产生数百个实例化——正常情况下这是可控的（每份实例化数 KB），但若键 struct 之上再叠泛型抽象层（如 `Cache<TKey,TValue>` 包装再包装），ILC 编译时长与体积可能超线性增长；另有值类型 + 泛型虚方法的组合在 ILC 下可能落到较慢的通用共享路径。

**缓解措施**：

1. 直译 Go 代码天然低泛型（Go 泛型使用克制），不引入 Go 侧没有的泛型抽象层（[05-conventions.md]：直译优先，禁自创框架）。
2. 结构键清单集中在 [03-checker-design.md]——键类型数量有上界且可盘点；容器组合以 `Dictionary`/`HashSet` 两种为主，不扩散。
3. 后备手段（仅当实测爆炸）：把非承重键改 sealed class（引用类型共享实例化），逐案记录且需论证 Equals/GetHashCode 语义不变（引用型成员引用相等 = Go 指针比较，主计划 §一.6）。
4. 观测：ILC 编译时长与 mstat 中泛型实例化 top-N 纳入体积趋势记录（同 R16）。

**检测手段**：阶段 0 起每阶段门禁包含三平台 `dotnet publish /p:PublishAot=true`——ILC 编译失败/超时即刻显形；体积与时长趋势（R16 同一管道）捕获渐进恶化。

---

### R18 — LSP 请求保序契约移植错误 → 竞态

- **类别** SEM ｜ **影响** 高 ｜ **概率** 中 ｜ **状态** 开放

**描述**。tsgo 的 LSP 服务器有一个精确的排序契约（`internal/lsp/server.go:532-591` `dispatchLoop`）：请求从 `requestQueue`（server.go:62-63,163-164 双动态队列）**串行**取出；每个请求的处理分两段——`handleRequestOrNotification` 在 dispatch goroutine 上**同步**执行（前奏：文档同步等状态突变必须按到达序生效），其返回的 `doAsyncWork` 闭包再 `go` 到新 goroutine（异步尾部：可并发、可经 `pendingClientRequests` 取消，server.go:545-552）。这个「同步前奏保序 + 异步尾部并发」的分界是行为契约：`didChange` 若与后续请求乱序，文档内容错乱，症状是 fourslash/LSP 测试**间歇性**失败。C# 直译陷阱：

1. 把整个 handler 包 `Task.Run` → 前奏失序；
2. 前奏方法签名写成 `async Task`，内部 `await`（哪怕只是日志 IO）→ continuation 切线程，后续请求的前奏与本请求前奏并发；
3. 异步尾部误持串行段的锁 → 死锁或吞吐塌陷；
4. 异步尾部直接调 checker → 线程池 1MB 栈，触发 R01。

**缓解措施**：

1. 结构直译：`dynamicQueue` → `System.Threading.Channels`（依赖映射，主计划 §一.5）；dispatch 循环是**单消费者**同步循环；前奏保持**同步**方法签名（返回 `Func<Task>?` 作为尾部），签名本身即防线——前奏里写不了 await。
2. 尾部进入 parse/bind/check 必须经 LargeStackExecutor 投递（联动 R01；[01-architecture.md] 线程模型）。
3. 取消语义直译：`pendingClientRequests` 字典 + per-request `CancellationTokenSource`，`$/cancelRequest` 路径与 Go 对齐（server.go:546-552,568-575 的 remove/cancel 时序）。
4. 回放测试脚本刻意构造乱序敏感序列（didChange 风暴 + 立即 completion；cancel 竞争完成），归 [08-testing-strategy.md] LSP 回放集设计。

**检测手段**：阶段 8 LSP stdio 回放集成测试（确定性脚本 + 输出序断言）；fourslash 全量在 CI 上重复运行（竞态呈间歇性，重复运行 + 压力回放是主要探针）；死锁由测试超时捕获。

---

### R19 — api 层句柄身份：活对象注册表与 GC 回收

- **类别** SEM ｜ **影响** 中 ｜ **概率** 低 ｜ **状态** 开放

**描述**。api 层（阶段 9）把 Symbol/Type/Node 以句柄形式暴露给外部客户端。tsgo 的机制（`internal/api/session.go`）：句柄是**整数/字符串 ID**，不是指针——`registerSymbol`（session.go:171）把 `*ast.Symbol` 登记进快照的 `symbolRegistry`，`resolveSymbolHandle`（session.go:231-245）/`resolveTypeHandle`（session.go:248-269）按 ID 查表，查不到返回 `ErrClientError`；节点句柄是索引式 `index.kind.path`（session.go:97 `nodeHandleFrom`，按需重建节点索引表）；同一 symbol 跨项目查询须返回同一句柄（session.go:44 注释）。Go 侧对象存活由 registry map 强引用自然保证。C# 直译时的风险形态：

1. 有人把 registry「优化」成 `WeakReference`/`ConditionalWeakTable` → 快照存活期间对象被 GC，句柄解析变成悬空；
2. registry 生命周期错绑（比请求短）→ 合法句柄提前失效；
3. ID 分配策略与 Go 不同步（复用已释放 ID）→ 旧句柄错误命中新对象——比失效更糟，是**身份**错乱。

主计划风险条目提示「GCHandle/字典强引用」两个选项：本设计中句柄始终是 ID + 字典查表，**字典强引用即充分**，GCHandle 仅在把托管对象指针交给非托管边界时才需要——本项目 api 传输层是 msgpack 帧（主计划 §二 阶段 9「手写 msgpack 帧直译」），不暴露指针，无 GCHandle 需求。

**缓解措施**：

1. 直译 registry 结构与生命周期：`Dictionary<SymbolID, Symbol>`/`Dictionary<TypeID, Type>` 由 snapshotData 等价物**强引用**持有，随快照释放整体丢弃（与 Go 同构）；[06-module-inventory.md] api 条目标注「registry 必须强引用，禁弱引用优化」。
2. ID 分配直译 Go 的递增策略（`Interlocked.Increment`），0 保留为空句柄（session.go:232-234,249-251 的空句柄错误路径一并直译）。
3. 「同 symbol 同句柄」不变量（session.go:44）写成显式断言测试。

**检测手段**：阶段 9 api 编码器往返测试 + tsgo 侧 api 测试移植（`session_apistate_test.go`、`session_completion_test.go` 等）；悬空/身份错乱在往返测试的句柄重解析断言中显形。

---

### R20 — 性能不达「≤2× tsgo」目标

- **类别** PERF ｜ **影响** 高 ｜ **概率** 中 ｜ **状态** 开放

**描述**。阶段 7 硬门禁：发布 AOT 二进制编译真实大项目，输出与 tsgo 全等且**耗时 ≤2× tsgo**。逆风因素盘点：UTF-16 字符串内存加倍 → 缓存命中率下降（R15）；gen2 大堆 GC 标记（R15）；C# 接口/虚调用与边界检查 vs Go 直接调用（AOT 去虚化能力有限）；`class` 对象头（8-16B/对象）vs Go struct 内嵌；LINQ/迭代器/闭包若渗入热路径则分配放大。顺风因素：AOT 无 JIT 预热；删 data 指针与 TypeData 层（主计划 §一.1/一.2）净省内存与间接跳转；Go 侧接口动态派发（`nodeData`/`TypeData`）在 C# 坍缩为字段直访。2× 预算并不宽裕但有兑现路径；真正的风险是**问题在阶段 7 才首次量化**，返工跨越已冻结的 5/6 阶段代码。

**缓解措施**：

1. **早测**：微基准从阶段 2 起随门禁建立（scanner 吞吐 vs tsgo、阶段 3 parser、阶段 5 checker 全量 conformance 耗时趋势），不等阶段 7；基准结果记录 [11-progress.md]（平时不设门禁，主计划 [08-testing-strategy.md] 性能基准定位）。
2. 热路径纪律（[05-conventions.md]）：Ts.Compiler 内禁 LINQ、禁热路径闭包捕获、`ReadOnlySpan<char>` 化 scanner/字符串工具；诊断/Unicode 表 RVA 静态数据零分配（主计划 §一.4）。
3. GC 与调优归 R15；剖析归因用 `dotnet-trace`/PerfView 对比 tsgo 的 pprof 火焰图逐热点核对（同一 conformance 语料）。
4. 若 2× 兜不住：优先个案优化（对象池、字段窄化、去虚化辅助 `sealed`），**不**放宽直译原则；放宽目标属主计划变更，须回主计划裁决（见「待澄清」）。
5. **观察项（[02-ast-design.md] §17-4 委托本册）**：mixin 字段经接口属性访问的分发开销——`node.FunctionLikeData().Type` 在 Go 是指针+字段偏移，C# 是虚调用+接口属性调用。剖析证实热路径退化时，对策为调用点局部改具体类型直访（不改设计）；阶段 7 性能门禁兜底。

**检测手段**：阶段 7 AOT 性能硬门禁（唯一硬指标）；阶段 2 起微基准趋势预警；阶段 10 BenchmarkDotNet + ServerGC 调优收尾（主计划 §二）。

---

### R21 — testdata 283MB 与子模块体积的仓库管理

- **类别** INFRA ｜ **影响** 低 ｜ **概率** 高 ｜ **状态** 开放

**描述**。实测 tsgo `testdata/` = **283MB**（含 `baselines/reference` 的 49,323 个基线文件——正确性「货币」，必须进 typescript-csharp 仓库）；`_submodules/TypeScript` 是完整 TS 官方仓库（clone 数百 MB，生成器输入 + fourslash 用例源）。风险：仓库膨胀导致 clone/CI checkout 变慢；万级小文件让 git status/IDE 索引变慢；conformance「accept」工作流一次可能改动数千基线文件，PR 审阅与合并冲突管理变难；若有人为省体积把基线挪出 git（LFS/外部存储），diff 可审性（主计划把「diff 可审」作为核心哲学）受损。

**缓解措施**：

1. 基线**以普通文本文件入库**（不 LFS）：字节级 diff 审阅是本项目的验收货币，git 原生 diff 不可替代；283MB 文本在 git 压缩后可控（基线高度相似，pack 压缩比好）。
2. 工程手段：CI 用 `--depth 1` + `--filter=blob:none` 部分克隆与 checkout 缓存；`_submodules/TypeScript` shallow + 按需初始化（只有生成器再生成与 fourslash 转换任务需要它，日常构建不需要）；开发者文档提供 sparse-checkout 配方（不常跑 conformance 的人可排除 testdata）。
3. `.gitattributes`：`testdata/** linguist-vendored`、基线目录 `-diff` 可选标记（大 PR 中折叠，需要时仍可显式查看）。
4. accept 工作流规范（[08-testing-strategy.md]）：基线变更独立成 commit、附带触发变更的代码 commit 引用、按目录分批提交，禁止与手写代码混在同一 commit。
5. 与 R12 联动：testdata 只在 rebase 窗口整体同步，避免碎片化更新制造历史膨胀。

**检测手段**：无硬门禁；CI clone/checkout 时长与仓库 pack 体积趋势监控（[11-progress.md]）；git 仓库超过阈值（建议 2GB pack）时触发维护任务（gc/重打包/历史审计）。

---

## 3. 交叉影响图（速查）

| 若此风险发生 | 会放大 / 触发 |
|---|---|
| R01 栈溢出 | R18（异步尾部换栈错误即触发 R01）、R14（fourslash 崩溃噪音） |
| R03 map 序 | R18（并发时序改变枚举序）、阶段 5 终检翻转 |
| R04 nil 语义 | R07（omitzero 破坏） |
| R09 垫片 | R08-C（\S 码元语义是同一根因） |
| R10 大小写 | R11（路径折叠共用实现） |
| R12 上游漂移 | 全部 SEM 类风险的基线参照系晃动；R09 休眠路径可能被激活；R21 testdata 同步量 |
| R15 GC/内存 | R20（性能门禁的最大单一变量） |
| R16/R17 体积 | 相互放大；共用观测管道 |

---

## 4. 待澄清（不改设计，仅登记）

1. **R20 兜底路径未定义**：主计划将「性能 ≤2× tsgo」列为阶段 7 硬门禁，但未规定不达标时的处置（放宽目标 / 允许偏离直译的优化 / 阻塞里程碑）。建议主计划补充失败分支的决策规则；本册第 4 条缓解措施暂按「个案优化优先、放宽须回主计划裁决」执行。
2. **R08-A4 残余偏差的验收口径**：RE2 与 `RegexOptions.NonBacktracking` 对奇异用户 pattern 的接受面差异不可能逐字节对齐（非基线承重）。建议在 [08-testing-strategy.md] 明确该点的验收口径（例如：fourslash autoImport 相关用例全过即视为达标，边角差异记录为已知偏差清单）。
3. **R08-A5 堆栈脱敏的直译边界**：`sanitizeStackTrace` 绑定 Go 栈格式，C# 侧必然重写解析逻辑——这与「逐函数直译」原则冲突的豁免范围（仅此函数？含其测试？）建议在 [06-module-inventory.md] 的 lsp 条目中明确。
4. **主计划行号勘误（存疑）**：主计划 §一.6 引用「sourcemap_recorder.go:351-353」，实测续行标记逻辑在 `internal/testutil/harnessutil/sourcemap_recorder.go:350-353`（文件位于 harnessutil 而非 tsbaseline，行号偏移 1 行以内）；「type_symbol_baseline.go:236」实测 underline pad 循环在 236-237。均为引用精度问题，不影响结论，rebase 窗口后需重新核行号。
5. **LargeStackExecutor 的 64MB 尺寸依据**：主计划定 64MB（对应 tsc.js --stack-size 思路），未给出推导。建议阶段 5 用最深 conformance 样例实测峰值栈深后在 [01-architecture.md] 记录余量系数；若 64MB 不足属主计划参数修订。
