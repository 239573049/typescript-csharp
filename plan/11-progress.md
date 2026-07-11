# 11 · 进度活文档（Living Progress）

| 元信息 | |
|---|---|
| 版本 | v1.1（结构版本，见 §9.5） |
| 日期 | 2026-07-11 |
| 状态 | Draft |
| 性质 | **活文档**——plan/ 中唯一被持续编辑的文件；其余 00–10、12 号文档为设计基线，仅在设计变更时修订 |
| 最近更新 | 2026-07-11（Claude Code 会话：vfs 核心分层落地 + 55 项单测，见变更日志） |
| 交叉引用 | 00-overview.md（范围/钉扎）· 06-module-inventory.md（包档案，本表各行的事实来源）· 08-testing-strategy.md（差分夹具/conformance 跑批器口径）· 09-phases.md（门禁定义，签核表的依据）· 12-risks.md（风险册，偏差登记的下游） |

---

## 0. 使命与读法

本文档回答一个问题：**移植现在到哪了**。它是全项目进度的唯一权威记录（single source of truth）：

- 06-module-inventory.md 定稿时把所有包的状态钉为「未开始」，并声明**此后状态只在本文档维护**（06 §0「初始状态」字段）。06 的表**不再更新**。
- 门禁定义在主计划 §二与 09-phases.md；本文档只记录门禁**何时以什么证据通过、谁签核**，不重复定义门禁内容。
- 每次移植会话结束时**必须**按 §9 更新规程更新本文档（本项目由 Claude Code 会话维护）。

**机器可读性约定**：本文档的表格是脚本与后续会话的解析接口。保持 Markdown 管道表格式；每包一行、不换行折行；列数不增删（结构变更须走 §9.5 版本规则）；历史表只追加不修改。

---

## 1. 状态图例

包状态取且仅取以下七值之一（或 `N/A`，仅限不移植的包）。状态针对**包**，不针对文件；包内文件级 WBS 见 10-phase-0-scaffolding.md 滚动波。

| 状态 | 进入条件（全部满足才可标注） |
|---|---|
| **未开始** | 包内没有任何 C# 产物（生成器/源文件/资源均未落仓） |
| **移植中** | 包的任一交付物（源文件直译、代码生成器、内嵌资源）已开始，但未达「编译通过」；或该包所属阶段已开工且包已被当前滚动波列入（备注注明实际起步情况） |
| **编译通过** | 06 清单中该包全部计划文件已移植；`dotnet build` 0 error；AOT/Trim analyzer 0 warning（07-aot-constraints.md 门禁）；下游包可引用 |
| **单测绿** | 06 清单登记的该包 Go 移植单测全部移植且在 JIT 下全绿（无测试的包：以备注「无单测」标注后此状态视同达成，直接可进下一档） |
| **差分绿** | 该包对应的 dumptool 差分夹具货币 100% 匹配（口径见 §4.1 映射表；无差分货币的包视同达成） |
| **基线绿** | 该包参与的 conformance / fourslash / tsc 基线门禁达到**所属阶段出口门禁**要求的档位（阶段 5/8 分级门禁以最终档 100%/99%+ 为准；无基线参与的包视同达成） |
| **完成** | 所属阶段出口门禁已在 §6 签核（含 AOT 发布二进制复跑，主计划 §四.2）；包内无遗留 TODO/桩；备注中无未闭环事项 |

**推进规则**：
1. 状态**单调递进**，不可跳档标注（可一次会话内连升多档，但每档条件都须真实满足）。
2. **回退**：上游 pin 升级、基线回归、发现移植错误时允许回退状态，回退必须在 §10 变更日志记一行（日期、包、从→到、原因）。
3. 「视同达成」的档位在状态列正常推进，但备注须注明依据（如「无单测」「无差分货币」）——不许静默跳档。
4. 跨阶段拆移的包（compiler、fswatch、tracing、testutil、ls/lsutil）：状态按**已到期部分**评定，备注标注拆移进度（如「阶段 4 子集编译通过；阶段 6 emitter 未开始」）。

**阶段级状态**（用于 §2/§3）：未开始 / 进行中 / 已签核，三态即可。

---

## 2. 总体快照

每次会话结束更新本节数字（计算口径固定如下，不得改口径）：

| 指标 | 当前值 | 口径 |
|---|---|---|
| 当前阶段 | **阶段 0 · 脚手架（进行中）** | 首个未签核阶段 |
| 包状态达「完成」 | 0 / 49 行（0 LOC / 298,374 可移植 LOC = **0.0%**） | §4 表中状态=完成的行；LOC 加权分母 = 全表 LOC 合计 298,474 − json 100（N/A 不移植） |
| 包状态达「编译通过」及以上 | 2 / 49 行（1,189 LOC = **0.4%** LOC 加权：collections、glob） | 同上，状态 ∈ {编译通过, 单测绿, 差分绿, 基线绿, 完成} |
| 最近 conformance 通过率 | —（尚未有跑批） | §5 历史表最新行 |
| 最近 fourslash 通过率 | —（尚未有跑批） | §5.2 历史表最新行 |
| 已签核门禁 | 0 | §6 行数 |
| 已登记偏差 | 6（另有 6 项设计期偏差待落地登记，§8.2） | §8.1 行数 |

> 注意：LOC 加权进度 ≠ 工期进度。主计划 §二的占比列是**工作量**估算（checker 33% 工作量但仅 21.3% LOC）；本表只报 LOC 口径，不做工期折算。

---

## 3. 阶段 0 交付物清单（进行中）

阶段 0 的交付物多数不是「包」，单列跟踪（三态：未开始/进行中/已完成）。出口门禁：三平台 `dotnet publish /p:PublishAot=true` hello-CLI 过；Ast/*.g.cs 过 AOT+Trim analyzer；诊断表计数=2,154（主计划 §二）。

| 交付物 | 状态 | 备注（2026-07-11 实况） |
|---|---|---|
| TypeScript.slnx + Directory.Build.props + Directory.Packages.props | 已完成 | 7e7e449 初始提交；W0-02 忽略规则已收敛；Release build/test 与 osx-arm64 Native AOT publish + 原生 `--version` 冒烟全绿 |
| 3 源项目 + 5 测试项目脚手架 | 已完成 | Ts.Compiler / Ts.LanguageService / Ts.Cli（含 hello Program.cs）；TestUtil / Compiler.Tests / Conformance.Tests / Fourslash.Tests / LanguageService.Tests |
| LargeStackExecutor（64MB 大栈线程，0 号风险对策） | 已完成 | src/Ts.Compiler/Core/LargeStackExecutor.cs + Version.cs 预移；tests/Ts.Compiler.Tests/Core/LargeStackExecutorTests.cs |
| plan/ 文档集 13 份 | 已完成 | 00–12 全部落库；09 已包含稳定门禁 ID `G<阶段>.<序号>` 体系；各文档中的待澄清项仍按既定流程跟踪 |
| _submodules/TypeScript 钉扎（与 tsgo 同 commit） | 已完成 | UPSTREAM.lock 双坐标 + testdata 指纹已提交（commit 9b728b8） |
| tools/generator/ 生成器自举：generate-csharp-ast.ts（ast.json+schema.ts 拷贝入仓） | 已完成 | 七文件+stringers 发射落库并幂等（commit cc149c8）；计数断言 192/35/351 过；G0.2 本地 gen:all 重跑 0 diff 实证通过（签核待远端 §6） |
| tools/generator/ 生成器自举：generate-diagnostics.ts | 已完成 | Messages.g.cs 计数=2,154 断言通过（commit 377c270） |
| tools/generator/ 生成器自举：generate-libs.ts + EmbeddedResource glob（108 个 lib.*.d.ts） | 已完成 | 108 个生成 + 内嵌（commit 377c270） |
| CI 管线（三平台构建 + AOT publish + analyzer 门禁） | 已完成 | .github/workflows/ci.yml（pin-and-drift + build-test×3 + aot-smoke×3）+ tools/check-upstream-pin.sh（sha256 + gen:all 漂移检查，本地跑通 G0.2 漂移 PASS）（commit 88f5cb9） |
| dumptool 骨架 + fixtures 存储决策（W0-22 / G0.4 / G0.5） | 已完成 | tools/dumptool/{patches/README.md,regen.sh}（--help/--dry-run 跑通，步骤 TODO(phase-2) 桩）；fixtures.lock.json（tsgoCommit 对齐 pin）；G0.5 裁定 Git LFS + .gitattributes 路由（commit 88f5cb9） |
| 阶段 0 出口门禁签核 | 待签核 | G0.2/G0.3 本地实证通过（gen:all 幂等、2,154 计数、0 警告）、G0.4/G0.5/G0.6 交付物就绪；G0.1 三平台 CI 待远端首跑；签核记入 §6 |

---

## 4. 包进度总表

事实列（阶段/LOC/映射/危险点）来源 = 06-module-inventory.md，LOC 为非测试 Go 行数含生成物；**本表只维护「状态 / 差分·基线通过率 / 备注」三列**，事实列变更须先改 06 再同步此处。任务口径「41 包」与实测 48 个 internal 目录的差异见 §11 待澄清第 1 条；本表 49 行全覆盖（48 internal + cmd/tsgo；json 为 N/A 不移植行）。

| 包 | 阶段 | LOC | 状态 | 差分/基线通过率 | 备注 |
|---|---:|---:|---|---:|---|
| bundled | 0 | 1,110 | 移植中 | — | generate-libs.ts 已落地：108 个 lib.*.d.ts 生成并以 bundled/libs/* 逻辑名内嵌（07 §5.2）；bundled:/// 覆盖 VFS 待 vfs 包（阶段 1） |
| diagnostics | 0 | 9,423 | 移植中 | — | generate-diagnostics.ts 已落地：Messages.g.cs 2,154 条计数断言通过（G0.3 实质达成，签核待 §6）；本地化面（Localize/Format/loc）后续阶段 |
| core | 1 | 2,669 | 移植中 | — | 已移植：text/tristate/stack/binarysearch/pattern/textchange/languagevariant/scriptkind/nodemodules/semaphore/workgroup（WorkGroup 并行实现走 LargeStackExecutor）+ LargeStackExecutor/Version 预移；单测全绿；待：core.go 大杂烩/bfs/compileroptions 族/context/linkstore 等；arena.go 不移植 |
| collections | 1 | 840 | 单测绿 | — | 全包 7 文件移植完（OrderedMap 流式 JSON 面按 10 §2.3 波次推迟）；7 个测试文件全绿；OrderedMap = map 序审计白名单容器 |
| tspath | 1 | 1,435 | 单测绿 | — | 全包移植完（65 函数 Go 序；Path 品牌 record struct）；1,189 行测试全移植 88/88 绿；连带 stringutil Compare/Runes 前置（与 Go 逐码点对齐的大小写折叠）；SplitVolumePath 因元组保留名 Rest 改 bool+out（PORT-NOTE） |
| vfs | 1 | 3,131 | 移植中 | — | 2026-07-11：核心分层落地（vfs.go/internal/iovfs/cachedvfs/wrapvfs/trackingvfs → Ts.Vfs.\*；`FS→IFileSystem` 依 05 §1.3，ReadFile→Try、写族→IOException、WalkDir→`Exception?` 值 + SkipDir/SkipAll 身份哨兵；io/fs → 自定义 IFileProvider 最小抽象；tspath 依赖以 internal TsPathShim 权宜内联，tspath 落地后删）；单测 55 项绿（含 decodeBytes BOM/UTF-16LE/BE 表驱动 21 例；vfs_test.go 仅基准→按 05 §16.1 归阶段 10）；待：osvfs/vfsmatch/vfstest/vfsmock；vfstest/vfsmock 归 Ts.TestUtil；osvfs OS 特定 |
| nativepath | 1 | 298 | 未开始 | — | 接口保形、实现换体（BCL ResolveLinkTarget），非逐行直译 |
| jsnum | 1 | 584 | 单测绿 | — | 全包移植完，686 用例绿；52.6 万例对 Go 直接对拍零失配（发现并修复 .NET 最短往返幂次二缺陷，证据入 R05）；G1.2 夹具生成可复用其差分脚本；遗留：NumberString.cs 内私有 stringutil 谓词镜像待并回 Ts.StringUtil |
| stringutil | 1 | 5,573 | 单测绿 | — | 全包移植完；两张表经 transcribe-unicode.ts 转写为 RVA .g.cs（11,364 行，fixtures/stringutil oracle 逐项校验）；全码点 SimpleFold/casing 对 Go 1.26.4 零分歧；WTF-8 哨兵机制在原生 UTF-16 下蒸发（签名保形）；50 测试绿 |
| glob | 1 | 349 | 单测绿 | — | Glob.cs 移植完，28 项单测（上游无单测，自建）；两处已登记偏差见 §8；由 tsoptions 基线间接再覆盖 |
| semver | 1 | 712 | 单测绿 | — | 全包移植完；1,106 行测试全移植（733 用例绿）；11 个正则 GeneratedRegex + RE2→.NET 四条翻译规则留档 |
| debug | 1 | 61 | 未开始 | — | 微包（06 §11.1），地基最早一批 |
| repo | 1 | 87 | 未开始 | — | 测试/生成器专用 → Ts.TestUtil/RepoPaths.cs，绝不进产品程序集 |
| json | 1 | 100 | N/A | — | 不移植：薄封装消失，调用点直用 STJ（06 §10.1） |
| scanner | 2 | 4,256 | 未开始 | — | 门禁：全语料 token 流 diff 100%；UTF-16 code unit 步进系统性改写 |
| ast | 3 | 20,847 | 移植中 | — | 生成面已落地（Ast/*.g.cs ×7 + stringers，11,924 行，计数与 Go 对齐，幂等，commit cc149c8）+13 个手写配套（Node 虚表/NodeList/Factory/Visitor/flags/Flow/Symbol·SourceFile 最小骨架）；待：ast.go 手写面全量与 jsdoc 桥；阶段 3 注意——BinaryExpression.setModifiers 已生成勿重复（ast.go:2056）、便捷访问器与 mixin 属性同名需遮蔽决策；positionmap.go 不移植 |
| parser | 3 | 9,071 | 未开始 | — | 深递归 0 号风险主现场；门禁：AST 转储 + 诊断(code,pos,len)三元组 100% |
| astnav | 3 | 783 | 未开始 | — | |
| testrunner | 3 | 889 | 未开始 | — | conformance 跑批器 = 阶段 5/6 门禁货币制造机 |
| testutil | 3–8 | 5,730 | 未开始 | — | 滚动移植（子包到位阶段见 06 §4.4）；race/ 不移植；含两个基架垫片 |
| binder | 4 | 3,555 | 未开始 | — | 门禁：symbol/flow 转储 100% |
| tsoptions | 4 | 6,144 | 未开始 | — | 全仓反射最密集：per-field switch 生成器统一供给 |
| module | 4 | 2,780 | 未开始 | — | 门禁：模块解析 trace 夹具匹配（日志逐字保真） |
| packagejson | 4 | 662 | 未开始 | — | |
| symlinks | 4 | 134 | 未开始 | — | |
| outputpaths | 4 | 307 | 未开始 | — | 无单测；emit 基线间接覆盖 |
| diagnosticwriter | 4 | 506 | 未开始 | — | 输出逐字符进 errors.txt 基线 |
| compiler | 4–6 | 5,658 | 未开始 | — | 拆移：阶段 4 fileloader/Program 子集 → 阶段 5 checkerpool → 阶段 6 emitter |
| checker | 5 | 59,822 | 未开始 | — | 22 个 partial 拆分表以 03-checker-design.md §4.2 为唯一权威（裁决 R1）；分级门禁 25→60→90→99.5→100%（errors.txt） |
| evaluator | 5 | 168 | 未开始 | — | 数字格式承重（const enum 内联） |
| nodebuilder | 5 | 78 | 未开始 | — | 微包；printer 生产依赖，须前置 |
| pseudochecker | 5 | 1,126 | 未开始 | — | 由 .d.ts 基线间接验收 |
| modulespecifiers | 5 | 2,280 | 未开始 | — | 候选说明符排序确定性承重 |
| printer | 6 | 11,442 | 未开始 | — | 只依赖 AST，可与阶段 5 部分并行 |
| transformers | 6 | 24,323 | 未开始 | — | 7 个子命名空间；变换链顺序逐项保真 |
| sourcemap | 6 | 1,013 | 未开始 | — | .js.map 字节门禁；列偏移 = UTF-16 |
| execute | 7 | 9,348 | 未开始 | — | 项目归属暂定 Ts.Cli（06 §13.2 待澄清）；.tsbuildinfo 字节保真全家桶 + xxh3 承重 |
| pprof | 7 | 169 | 未开始 | — | 非 1:1：EventPipe/.nettrace（设计期偏差，落地时登记 §8） |
| cmd/tsgo | 7–9 | 359 | 未开始 | — | Ts.Cli 入口三路复用（tsc/--lsp/--api），随阶段 7/8/9 分步接线（06 §11.4） |
| ls | 8 | 38,933 | 未开始 | — | lsutil 子集须提前至阶段 7（execute 依赖，06 §13.1） |
| format | 8 | 4,291 | 未开始 | — | 门禁项：format 自有套件；规则表初始化顺序承重 |
| lsp | 8 | 21,008 | 未开始 | — | lsp_generated 17,262 行 → Protocol.g.cs（generate-lsp.ts）；structcodec 反射消失 |
| project | 8 | 11,016 | 未开始 | — | 测试 11,603 行全移植；dirty/logging 子包先于 ls 根 |
| fswatch | 7–8 | 5,446 | 未开始 | — | 非 1:1：FileSystemWatcher 整包重写；最小后端提前阶段 7（06 §13.3） |
| jsonrpc | 8 | 287 | 未开始 | — | |
| fourslash | 8 | 9,770 | 未开始 | — | 基架；289 顶层手写 + 210 manual + 3,796 生成用例 = 4,295（裁决 R10；convertFourslash.mts 改发射 C#） |
| api | 9 | 9,110 | 未开始 | — | msgpack 帧手写直译零依赖；门禁：编码器往返测试 |
| tracing | 4/9 | 763 | 未开始 | — | 非 1:1：EventSource；接口骨架须提前至阶段 4/5（checker/compiler 生产 import） |
| locale | 9 | 28 | 未开始 | — | 微包；骨架随阶段 3 存在；不用 CultureInfo |

**合计**：49 行，298,474 LOC（可移植 298,374，json 100 不移植）。

### 4.1 「差分/基线通过率」列口径映射

该列的百分比对不同包意义不同，按下表解释；填写格式见 §9.3。无货币的包恒为 `—`（状态图例中「视同达成」处理）。

| 包 | 该列口径 |
|---|---|
| scanner | dumptool token 流夹具匹配率（kind,pos,end,text 四元组，全语料） |
| ast / parser / astnav | AST 结构转储匹配率 + 解析诊断三元组匹配率（两数并记：`转储%/诊断%`） |
| binder | symbol/flow 转储匹配率 |
| tsoptions | tsoptions 基线测试通过率 |
| module | 模块解析 trace 夹具匹配率 |
| checker / evaluator / nodebuilder / pseudochecker / modulespecifiers | conformance `.errors.txt` 字节匹配率（阶段后期并记 `.types`/`.symbols`） |
| printer / transformers / sourcemap / compiler(emitter) | conformance `.js`/`.d.ts`/`.js.map` 字节匹配率（合并口径，§11 第 4 条） |
| execute / cmd/tsgo | tsctests 移植套件通过率 + .tsbuildinfo 字节相等 |
| ls / format / lsp / project / fswatch / fourslash | fourslash 通过率（format 另记自有套件） |
| api | 编码器往返测试通过率 |
| 其余（core、collections 等地基包） | —（以移植单测绿为准，无独立差分货币） |

---

## 5. conformance 通过率历史表

**追加式**，每次全量 conformance 跑批后追加一行（分级门禁的阶段性跑批也记）。百分比口径：字节全等基线文件数 / 适用基线文件数（分母基数 = testdata/baselines/reference 的 49,321 个 Git 跟踪文件中该类别适用者；跑批器口径细则见 08-testing-strategy.md）。js % 列合并计 .js/.d.ts/.js.map 三类 emit 基线（拆列问题见 §11 第 4 条）。

| 日期 | errors.txt % | types % | symbols % | js % | 备注（commit / 跑批范围 / 档位） |
|---|---:|---:|---:|---:|---|
| | | | | | |

<!-- 行模板：| 2026-MM-DD | 62.4%（xx,xxx/xx,xxx） | — | — | — | abc1234；阶段 5 第二档（目标 60%）达成 | -->

## 5.2 fourslash 通过率历史表

**追加式**，每次 fourslash 全量跑批后追加一行。用例三段口径按裁决 R10 统一：289（顶层手写）+ 210（tests/manual/）+ 3,796（gen/）= 4,295。分母口径待 08-testing-strategy.md 定稿（是否扣除 manualTests/unparsedTests/crashingTests 清单，§11 第 5 条）；skip 记账走 failingTests.txt 机制（06 §8.7）。

| 日期 | 手写用例 %（n=289） | manual 用例 %（n=210） | 生成用例 %（n=3,796） | 总体 %（n=4,295） | failingTests.txt 条数 | 备注（commit / 档位） |
|---|---:|---:|---:|---:|---:|---|
| | | | | | | |

<!-- 行模板：| 2026-MM-DD | 91.0%（263/289） | 90.0%（189/210） | 88.2%（3,348/3,796） | 88.5% | 473 | abc1234；阶段 8 第二档（目标 90%）未达 | -->

---

## 6. 门禁签核日志表

**追加式**，仅在阶段出口门禁（或阶段 5/8 的分级子门禁）**通过**时追加；门禁内容以 09-phases.md 定义为准（09 已落库 v1.1，含稳定门禁 ID `G<阶段>.<序号>` 体系；本表「门禁」列一律用该 ID）。证据链接 = commit hash（短 7 位）+ CI run URL 或仓库内日志相对路径；签核规则见 §9.4。

| 阶段 | 门禁 | 日期 | 证据链接 | 签核 |
|---|---|---|---|---|
| | | | | |

<!-- 行模板：| 0 | 三平台 AOT hello-CLI publish + Ast/*.g.cs analyzer + 诊断计数=2,154 | 2026-MM-DD | abc1234 + <CI URL 或 artifacts/gate-0.log> | 用户确认（会话 2026-MM-DD）| -->

---

## 7.（编号预留）

> 为保持既有交叉引用稳定，§7 预留空节；偏差登记见 §8。结构调整时按 §9.5 版本规则统一处理。

## 8. 已知偏差登记表

**与 tsgo 行为的一切有意偏差必须在此登记并链接理由**（主计划批准的设计、06/07/12 的对应小节、或用户在会话中的明确确认）。未登记的行为差异一律视为 bug 处理。

### 8.1 登记表（追加式，当前为空）

| 编号 | 登记日期 | 包/文件 | 偏差描述 | 影响面（基线/行为/格式） | 理由与批准链接 | 状态 |
|---|---|---|---|---|---|---|
| D-001 | 2026-07-11 | glob/Glob.cs | `[!…]` 的 negate 解析后匹配中不使用——保 Go 上游原样（glob.go:294-301） | 行为（与 Go 逐位一致；非基线） | 1:1 铁律；上游自注 testing-only | 生效 |
| D-002 | 2026-07-11 | glob/Glob.cs | 尾斜杠模式耗尽输入时 Go 会越界 panic（glob.go:233），C# 加守卫返回 true | 行为（Go 崩溃路径→合法匹配；非基线） | 上游潜在 bug，AGENTS §6 保守处置 | 生效 |
| D-003 | 2026-07-11 | ast 生成器 | 可空性按 04 §2.2 横切规则（Go 指针→全可空），不采纳 02 §8.1/8.3 示例的 optional 非空推断 | 类型签名（编译期；非基线） | 实证三处 member/base optional 不一致会迫使 `!` 扩散 | 生效 |
| D-004 | 2026-07-11 | tools/generator | 输入副本置于 `_scripts/` 子目录而非 04 §1.1 平铺——schema.ts 以相对路径定位 ast.json，平铺须打补丁破坏 sha256 钉扎 | 工作区布局（非基线） | 保逐字节副本优先；README 已注记 | 生效 |
| D-005 | 2026-07-11 | vfs/Internal/Common.cs | DecodeBytes 无 BOM 路径对**无效 UTF-8** 的替换粒度：Go 按 rune 迭代逐无效字节产一个 U+FFFD，.NET Encoding.UTF8 按最大无效子序列产一个 U+FFFD；有效 UTF-8 双方逐字符等价 | 行为（仅无效 UTF-8 源文件；conformance 语料待核） | C# string=UTF-16 边界（plan/01），BCL 解码器语义；AGENTS §6 登记备查 | 生效 |
| D-006 | 2026-07-11 | vfs/FileProvider.cs | IFileProvider 失败以 null 表达、不携错误明细；WalkDir 算法在 stat/ReadDir 失败时给回调传 `VfsUtil.ErrNotExist` 哨兵而非 Go 的 \*PathError | 行为（walkFn 的 err 身份；tsgo 生产 walkFn 均只判空/吞掉，唯 fourslash 基架 errors.Is(fs.ErrNotExist)——阶段 9 前重估） | plan/05 §4 就地消费判据；survey 证据见 vfs 移植会话 | 生效 |

<!-- 行模板：| D-001 | 2026-MM-DD | pprof | profile 输出为 .nettrace 而非 .pb.gz | CLI 产物格式（非基线） | 06-module-inventory.md §7.2；主计划 §一.4 精神 | 生效 | -->
<!-- 编号规则：D-XXX 递增，不复用；状态 ∈ {生效, 已消除（回归 1:1）, 已升级为风险（转 12-risks.md）} -->

### 8.2 设计期已批准偏差（预告，落地时正式登记）

以下偏差已由主计划/06/07 在设计期批准，**尚未产生运行产物**；对应包首次达「编译通过」时，负责会话必须将其转正为 §8.1 的 D-XXX 条目：

1. **pprof → EventPipe/.nettrace**（输出格式偏离 .pb.gz，flag 名保留）——06 §7.2。
2. **tracing → EventSource**（不复刻 Chrome trace_event JSON 字节；types.json 保真待 06 §13.4 澄清）——06 §9.2。
3. **fswatch 平台后端 → FileSystemWatcher 整包重写**（事件合并/丢事件语义与 Go 后端不同）——06 §8.5、12-risks.md。
4. **nativepath 接口保形、实现换体**（BCL ResolveLinkTarget；Windows 尾随斜杠/短路径行为差异）——06 §2.7。
5. **testutil/race 不移植**（.NET 无 race detector 构建标签对应物）——06 §4.4。
6. **type_symbol_baseline.go:236 字节 pad 休眠路径不移植**（记 12-risks.md，不复刻该未启用行为）——06 §4.4。

> 注意：PositionMap 删除、arena 删除、mixin 按字段名去重等属于**实现结构**差异而非可观察行为偏差（基线输出全等），不登记在此；其正当性论证在主计划 §一 与 01/02 号文档。若日后发现它们产生可观察差异，立即按 §8.1 登记并升级 12-risks.md。

---

## 9. 更新规程

### 9.1 维护者与时机

- **维护者**：执行移植工作的 Claude Code 会话。**每次移植会话结束时必须更新本文档**——这是会话的收尾义务，与代码提交同批（同一 commit 或紧随的 docs commit）。
- 只读会话（纯调查/答疑，未改动仓库代码与状态）可不更新，但若调查推翻了某包状态（如发现已标「单测绿」的包有红测试），必须回退状态并记变更日志。

### 9.2 每会话必查清单（按序执行）

1. **§4 包进度总表**：更新本会话触及的每个包的「状态」列（按 §1 图例严格评定）与「备注」列；有差分/基线跑批结果的更新「差分/基线通过率」列。
2. **§3 阶段 0 交付物清单**：阶段 0 期间同步勾进度；阶段 0 签核后本节冻结。
3. **§2 总体快照**：重算全部指标（口径固定，见表内说明）。
4. **§5 / §5.2 历史表**：本会话若跑了全量 conformance 或 fourslash，各追加一行（未跑不补空行）。
5. **§6 门禁签核**：本会话若有门禁通过且完成签核（§9.4），追加一行。
6. **§8 偏差登记**：本会话若引入/落地/消除有意偏差，登记或更新状态；触发 §8.2 预告转正的执行转正。
7. **§10 变更日志**：追加**恰好一行**本会话摘要（多包更新合并入一行）。
8. **元信息块**：更新「最近更新」字段（日期 + 一句话）。版本号仅按 §9.5 变更。

### 9.3 格式约定

- **日期**：`YYYY-MM-DD`。
- **百分比**：保留 1 位小数并括注绝对数，如 `99.5%（49,074/49,321）`；未跑批用 `—`，不用 0%（0% 表示跑了且全挂）。
- **状态值**：只能取 §1 七值或 `N/A`，不得自创（「进行中」只用于阶段级/交付物级三态，不用于包）。
- **备注列**：格式 `YYYY-MM-DD：内容`；最新在前，最多保留 3 条，挤出的旧条目移入 §10 变更日志。设计期固定备注（如「非 1:1 重写」）不带日期、常驻。
- **证据链接**：优先 `commit 短 hash（7 位）`；CI 建成后附 run URL；本地跑批日志放仓库 `artifacts/`（或 09-phases.md 指定位置）并用相对路径引用。
- **表行纪律**：§4 不删行不加行（包集合变更须先修订 06 并走 §9.5）；历史表/签核表/偏差表只追加、不改写既有行（笔误修正除外，修正须记变更日志）。

### 9.4 签核规则（暂行，见 §11 第 2 条）

- 门禁通过的**事实判定**由 Claude Code 会话依 09-phases.md 的出口条件执行并出示证据（测试输出/CI 链接/diff 统计）。
- 「签核」列**必须含用户确认**：会话在门禁证据出示后向用户请求确认，记录形如 `用户确认（会话 2026-MM-DD）`；未获用户确认前门禁不得记入 §6，相关包状态停在「基线绿」不得标「完成」。
- 主计划未定义签核权责，此为本文档暂行规则，待澄清后修订。

### 9.5 本文档自身的版本规则

- **内容更新**（改状态/追加历史行/改备注）：不 bump 版本，只改元信息「最近更新」。
- **结构变更**（增删列、增删节、改口径定义）：bump 次版本（v1.1、v1.2…），变更日志说明动机；口径变更须保证历史行仍可解释（必要时在历史表备注列注明「口径自 vX.Y 变更」）。
- 06-module-inventory.md 的事实修订（LOC 复测、阶段调整、待澄清裁决）落地时，本表事实列同步，记变更日志。

---

## 10. 变更日志（追加式，最新在前）

| 日期 | 版本 | 会话/作者 | 摘要 |
|---|---|---|---|
| 2026-07-11 | v1.1 | Claude Code 会话（vfs 核心分层） | port(vfs)：vfs.go/internal/iovfs/cachedvfs/wrapvfs/trackingvfs → src/Ts.Compiler/Vfs/\*（IFileSystem/Entries/IFileInfo/IDirEntry/FileMode、IFileProvider 垫片、TsPathShim 权宜内联）+ tests/Vfs 6 文件 55 测试绿（decodeBytes 表驱动 21 例）；偏差 D-005/D-006 登记；vfs 状态→移植中；快照两指标依 §9.2 重算修正（编译通过≥ 2 行、已登记偏差 6——前会话漏更）；待澄清 +1（01 §9.2 IVfs 字样）；验证时共享构建暂被并行 tspath/stringutil 在途文件阻塞，Vfs 文件本身 0 警告 0 错误（隔离工程全绿），未提交 commit（任务指令） |
| 2026-07-11 | v1.1 | Codex 会话（阶段 0 脚手架） | 完成 W0-02/W0-01：收敛 `.gitignore`，修正 xUnit v3 空测试壳与 Ts.TestUtil 类库边界，建立初始提交 7e7e449；Release build/test、osx-arm64 Native AOT publish 及原生 `tscs --version` 冒烟全绿；plan/ 00–12 文件集齐全 |
| 2026-07-11 | v1.1 | Claude Code 会话（评审修复） | 评审修复：06 交叉引用随其重编号修正（§14.x→§13.x；§12.1/§12.4→§11.1/§11.4）；checker 拆分表权威改指 03 §4.2 的 22 partial（裁决 R1）；ast 生成物 ×5→×7（裁决 R2）；fourslash 三段计数统一 289+210+3,796=4,295 并为 §5.2 增列 manual 列（裁决 R10，结构变更故 bump v1.1）；§3 文档状态重算——00–08、10–12 已落库，09 标补写中（裁决 R20） |
| 2026-07-11 | v1.0 | Claude Code 会话 | 骨架建立：图例/49 行包总表/历史表/签核表/偏差登记/更新规程；阶段 0 交付物按仓库实况盘点（slnx+项目脚手架+LargeStackExecutor 已完成，生成器/submodule/CI 未起步） |

---

## 11. 待澄清（呈报主计划维护者，不擅改设计）

1. **「41 包」计数口径**：任务与主计划口径为 41 包，06-module-inventory.md §13.1 实测为 45 个列名包 + 3 个微包 = 48 个 internal 目录（另有 cmd/tsgo）。本表按 49 行全覆盖（含 json 的 N/A 行），待主计划统一计数后仅改措辞、不改行集。
2. **门禁签核权责未定义**：主计划 §二 只定义门禁内容，未定义谁有权签核。本文档 §9.4 暂行「会话出证据 + 用户确认」，需主计划确认或指定替代机制（如 CI 绿即自动签核）。
3. **门禁命名依赖 09-phases.md**（已关闭）：09-phases.md 已落库（v1.1），含稳定门禁 ID `G<阶段>.<序号>` 体系；签核表「门禁」列与 §4.1 口径映射统一使用该 ID。
4. **conformance 历史表 js % 列口径**：主计划阶段 6 门禁为 `.js`/`.d.ts`/`.js.map` 三类 100%，任务给定的历史表列仅「js %」。本文档暂以合并口径计（三类合并的字节匹配率）；若 08/09 号文档要求分列跟踪，v1.1 加列。
5. **fourslash 通过率分母**：manualTests.txt / unparsedTests.txt / crashingTests.txt 清单内用例是否计入分母（tsgo 自身也不跑其中部分），需 08-testing-strategy.md 定稿口径；定稿前 §5.2 按「全部用例」为分母、清单跳过计为不通过的保守口径。用例总数三段口径已按裁决 R10 关闭（289+210+3,796=4,295，§5.2 已增列 manual 段），本条仅余分母扣减问题。
6. **阶段 0 包条目的状态字面**：任务指令要求阶段 0 条目状态=进行中，本表以图例值「移植中」承载（bundled/diagnostics 两行），但其包本体尚无产物，与 §1 图例的严格进入条件有出入——首个生成器落地后自然吻合；若主计划希望包状态与阶段状态分离表达，可在 v1.1 为 §4 增加「阶段状态」视图。
7. **vfs.FS 接口定名 `IFileSystem` 的文档同步**：05 §1.3/§4.2 钦定 `IFileSystem`（05:1197 自注留待落型确认），vfs 落地已采用；plan/01 §9.2 表格残留 `IVfs` 字样，已在 01 §10 待澄清第 7 条登记，请 01 下次修订同步。相关命名连带裁决（本会话既成事实，供追认）：包级函数/变量宿主循 CoreUtil 先例（`VfsUtil`/`IoVfsUtil`/`WrapVfsUtil`），具体 FS 类循 01 §9.2 BundledWrapFs 先例（`CachedFs`/`TrackingFs`/`WrappedFs`/`IoFs`）；C# 保留元组名致 `SplitPath` 元素 `Rest→RestPath`。
