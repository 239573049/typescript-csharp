# 11 · 进度活文档（Living Progress）

| 元信息 | |
|---|---|
| 版本 | v1.1（结构版本，见 §9.5） |
| 日期 | 2026-07-11 |
| 状态 | Draft |
| 性质 | **活文档**——plan/ 中唯一被持续编辑的文件；其余 00–10、12 号文档为设计基线，仅在设计变更时修订 |
| 最近更新 | 2026-07-13（Claude Code 会话：**conformance 语料 16→815 例、字节匹配 26.5%（阶段5 第一档 25% 达成）**；curated 12/16；#22 awaited-types（TS2349 探针转正）；LSP server 骨架（tscs --lsp）；ls references/signatureHelp/completions；§5 历史表首行落笔；见变更日志） |
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
| 当前阶段 | **阶段 5–8 多线并进**：checker 22 partial 全面实质落地（+awaited-types #22/属性访问/函数符号/typeFacts/调用解析/上下文类型/拓宽/别名标记/全局合并/lib-reference 加载）——**双里程碑达成：e2e 类型错误端到端返回 + 真实 .ts→.js emit**（tscs 是真编译器）；**conformance 全语料 2,619 例字节匹配爬坡至 90.0%（阶段 5 第一/二/三档 25%/60%/90% 全部达成，§5 历史表）**，curated 12/16，语料扩容 ~2,619 例 + 全量跑批在途；**阶段 6** printer/transformers/sourcemap 全落地 + emit 管线接线；**阶段 7** CLI tscs 类型检查+emit+tsconfig；**阶段 8** LanguageService（jsonrpc/format/quickinfo/definition/completions/references/signatureHelp）+ **LSP server 骨架（tscs --lsp）**；**跨阶段** fswatch/tracing/pprof/pseudochecker/diagnosticwriter/locale；余：匹配率爬坡（failingCases 628 例逐波消化）/.d.ts 签名序列化/execute watch+build/api/fourslash；阶段 0 待远端 CI | 首个未签核阶段 |
| 包状态达「完成」 | 0 / 49 行（0 LOC / 298,374 可移植 LOC = **0.0%**） | §4 表中状态=完成的行；LOC 加权分母 = 全表 LOC 合计 298,474 − json 100（N/A 不移植） |
| 包状态达「编译通过」及以上 | ~30 / 49 行（阶段 1–4 主体 + checker/evaluator/nodebuilder/sourcemap/diagnosticwriter/locale/printer/transformers/fswatch/tracing/pprof/pseudochecker/jsonrpc/format/execute；§4 逐行细化下次会话补） | 同上，状态 ∈ {编译通过, 单测绿, 差分绿, 基线绿, 完成} |
| 最近 conformance 通过率 | **99.7%（2610/2619，2026-07-16，确定性，零 stub-崩溃案）★ 阶段 5 第四档已过 ★——B11 伪类型子系统 + Any-带别名臂 + @type reparse 补全,三修复共翻 9 案；余 9 全为字节 diff（推断过报 4、getSymbolChain 2、其余 3）** | §5 历史表最新行 |
| 最近 fourslash 通过率 | —（尚未有跑批） | §5.2 历史表最新行 |
| 已签核门禁 | 0 | §6 行数 |
| 已登记偏差 | 8（D-001…D-008；另有 6 项设计期偏差待落地登记，§8.2） | §8.1 行数 |

> **差分主干环境阻塞（2026-07-11，R24）**：dumptool 需 tsgo 可写 git 检出跑补丁分支，当前为只读非 git 快照——G2.1（token 流）/G3.2（AST 转储）/G4.1（symbol·flow）全语料差分门禁的夹具无法生成。阶段 2–4 移植以自建单测覆盖并推进，差分门禁记为「待夹具」，待环境提供 tsgo git 检出后补跑签核。
>
> **阶段 1 地基状态（2026-07-11）**：11 个包全部单测绿（core/collections/tspath/vfs/nativepath/jsnum/stringutil/glob/semver/debug/repo），json 为 N/A。G1.1（移植单测全绿）达成；G1.2（jsnum 四向对拍）已以 52.6 万例对 Go 零失配实证，形式化 100 万夹具门禁待 dumptool jsnum 导出器；G1.3（tspath 双路径）88/88；G1.4（stringutil 表与 Go 一致）全码点零分歧。文档化延后：OrderedMap 流式 JSON 面、core.StringifyJson。
>
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
| core | 1 | 2,669 | 单测绿 | — | 全包主体移植完：小件族 + CompilerOptions 家族（130 字段声明序保真）+ core.go ~70 助手 + bfs + linkstore + context(RequestContext) + LargeStackExecutor/Version；242 Core 测试绿；UTF-16 世界观（TextPos→int/UTF16Len→Length/ECMALineStarts 按 code unit）；StringifyJson 与 ShouldRewriteModuleSpecifier 文档化隔离（前者延后 Utf8JsonWriter，后者依赖已到位可择时恢复）；arena.go 不移植；宿主类不一致：BinarySearch 在 Ts.Core.Core、core.go 助手在 Ts.Core.CoreUtil（待 06 统一） |
| collections | 1 | 840 | 单测绿 | — | 全包 7 文件移植完（OrderedMap 流式 JSON 面按 10 §2.3 波次推迟）；7 个测试文件全绿；OrderedMap = map 序审计白名单容器 |
| tspath | 1 | 1,435 | 单测绿 | — | 全包移植完（65 函数 Go 序；Path 品牌 record struct）；1,189 行测试全移植 88/88 绿；连带 stringutil Compare/Runes 前置（与 Go 逐码点对齐的大小写折叠）；SplitVolumePath 因元组保留名 Rest 改 bool+out（PORT-NOTE） |
| vfs | 1 | 3,131 | 单测绿 | — | 全包移植完：核心分层 + osvfs（大小写探测/Realpath BCL LinkTarget/reparse→symlink）+ vfsmatch（生产 include/exclude 主引擎，MATCHING_ALGORITHM 内嵌）+ vfstest→Ts.TestUtil；TsPathShim 删除换 Ts.TsPath；116 Vfs 测试绿；vfsmock 不移植（FakeFs 替身）；VfsMatch 私有 Every/LastOrNil/Flatten 待重定向 Ts.Core.CoreUtil；osvfs Realpath 待 nativepath 落地后收敛 |
| nativepath | 1 | 298 | 单测绿 | — | Ts.NativePath 抽取（Go per-OS syscall 优化坍缩为单 BCL EvalSymlinks + ReparsePoint 检测）；osvfs 委托调用；3 单测绿（Windows 符号链接跳过） |
| jsnum | 1 | 584 | 单测绿 | — | 全包移植完，686 用例绿；52.6 万例对 Go 直接对拍零失配（发现并修复 .NET 最短往返幂次二缺陷，证据入 R05）；G1.2 夹具生成可复用其差分脚本；遗留：NumberString.cs 内私有 stringutil 谓词镜像待并回 Ts.StringUtil |
| stringutil | 1 | 5,573 | 单测绿 | — | 全包移植完；两张表经 transcribe-unicode.ts 转写为 RVA .g.cs（11,364 行，fixtures/stringutil oracle 逐项校验）；全码点 SimpleFold/casing 对 Go 1.26.4 零分歧；WTF-8 哨兵机制在原生 UTF-16 下蒸发（签名保形）；50 测试绿 |
| glob | 1 | 349 | 单测绿 | — | Glob.cs 移植完，28 项单测（上游无单测，自建）；两处已登记偏差见 §8；由 tsoptions 基线间接再覆盖 |
| semver | 1 | 712 | 单测绿 | — | 全包移植完；1,106 行测试全移植（733 用例绿）；11 个正则 GeneratedRegex + RE2→.NET 四条翻译规则留档 |
| debug | 1 | 61 | 单测绿 | — | 吸收为 Ts.Core.Debug 静态类（DebugFailureException←panic）；4 项单测绿（commit 286d047） |
| repo | 1 | 87 | 单测绿 | — | Ts.TestUtil/RepoPaths.cs（slnx 标记根遍历 + xunit v3 动态 skip）；无产品程序集引用；无单测视同达成（commit 286d047） |
| json | 1 | 100 | N/A | — | 不移植：薄封装消失，调用点直用 STJ（06 §10.1） |
| scanner | 2 | 4,256 | 单测绿 | — | 全包移植完（scanner/regexp/unicodeproperties/utilities，UTF-16 世界观：char→text[pos]、DecodeRuneAt、PositionMap 删除）；65 单测绿；十六/二/八进制 TokenValue 经 jsnum 归一十进制（tsgo 行为）；两处 UTF-16 规格差异 PORT-NOTE（正则裸 LS/PS、pendingLowSurrogate 蒸发，09 §5.6 授权取移植口径）；AST 依赖函数（行图/注释范围）顺延阶段 3；**G2.1 全语料 token 流差分待 dumptool tokens 导出器就绪后另跑签核** |
| ast | 3 | 20,847 | 移植中 | — | 生成面（7 .g.cs）+ 手写面已落地：utilities.go ~113 谓词/导航、30 个 §4.4 switch 访问器 + 便捷访问器（53 扩展方法，解 CS0108 遮蔽）、diagnostic/precedence/deepclone、checkflags/symbolflags/functionflags、SourceFile 全体 + UTF-16 行图；补全 scanner 顺延的 text-of-node 函数；**待阶段 4**：Symbol 依赖的 utilities 后半（Symbol 仍骨架）+ LS/parser 钩子；positionmap.go 不移植 |
| parser | 3 | 9,071 | 单测绿 | — | **全语法移植完毕、Parser/ 零 NotImplementedException 桩**（foundation + 6 簇 382 方法 + 5 残留）；~200 单测（各簇端到端 AST 树）+ lib.*.d.ts 集成 smoke（es5.d.ts ~2500 行零诊断解析）；深递归经 LargeStackExecutor；唯 JS 赋值声明 @type/@satisfies reparse 子例 // !!! 延后（依赖 GetAssignmentDeclarationKind 阶段 4）；G3.1/G3.2 全语料签核待 R24；增量解析不移植 |
| astnav | 3 | 783 | 单测绿 | — | tokens.go→Ts.AstNav.Tokens（19 函数，find-node-at-position + JSX << 重扫描）；补落 ast-face 延后的 ast 表面（token 缓存/VisitEachChild/UpdateSourceFile/JSDoc 谓词）；11 测试绿 6 待 parser；UTF-16 代理对验证；§1.5 global:: 修正 |
| testrunner | 3 | 889 | 移植中 | — | test_case_parser 已移植（Ts.TestUtil/TestCaseParser.cs）；compiler_runner 驱动待阶段 4 Program |
| testutil | 3–8 | 5,730 | 移植中 | — | baseline（逐字节比对 + patience diff + accept 流）+ test_case_parser（// @filename 多文件拆分）已落地（Ts.TestUtil/Baseline·Diff·TestCaseParser）；compiler_runner/tsbaseline/harnessutil 编译驱动待阶段 4 Program；race/ 不移植 |
| binder | 4 | 3,555 | 单测绿 | — | binder.go/nameresolver/referenceresolver 全量 + Symbol 完整体（SymbolTable=Dictionary ordinal）+ 补全 ast/scanner binder 前置；CFG 流图构造；BindSourceFile 经 LargeStackExecutor；28 测试；G4.1 symbol/flow 转储待 R24 |
| tsoptions | 4 | 6,144 | 单测绿 | — | 2026-07-12：**tsconfig.json 加载收官**——JSON-source 走查(convertConfigFileToObject/convertToJson/convert{Object,Array}LiteralExpressionToJson/ForEach{TsConfigPropArray,PropertyAssignment}，经 ast.TryGetTextOfPropertyName + 新增 AstUtilities.IsComputedNonLiteralName) + ParseJsonConfigFileContent/Worker(组合 compilerOptions/files/include/exclude/references) + extends 单/多层链(getExtendedConfig→§1.5 global::Ts.Module.Module.ResolveConfig，relative/module 两分支) + parseConfig(循环检测/合并) + getFileNamesFromConfigSpecs(vfsmatch.ReadDirectory+扩展优先级) + ${configDir} 替换 + GetParsedCommandLineOfConfigFile{,Path} + ParsedCommandLine 输出面(CommonSourceDirectory/GetOutputFileNames/GetBuildInfoFileName/PossiblyMatchesFileName/ParseInputOutputNames/ReloadFileNamesOfParsedCommandLine，实现 outputpaths.OutputPathsHost + glob)；删 3 TsConfigParsing 桩 + 6 ParsedCommandLine 桩；TsConfigParsing.{Load,Worker}.cs 两 partial；15 新测(compilerOptions/files/include-exclude 通配/单层 extends 合并+覆盖/references/output/CSD/configDir)——**59 TsOptions 测试全绿**；唯余 locale.Parse 桩(validateJsonOptionValue extraValidationLocale + ParsedBuildCommandLine.Locale，phase 9)；隔离 worktree(HEAD)build 0/0；G4.2 基线待跑批器 | 主体移植 + 44 测试绿：CommandLineOption/声明表/enummaps/namemap/parseCompilerOptions 130 字段 switch/mergeCompilerOptions 反射→手写 per-field 表；集成方修正 672 处外层命名空间遮蔽（§1.5 强化规则）|
| module | 4 | 2,780 | 单测绿 | — | resolver node16/nodenext/bundler 全算法 + exports/imports/typesVersions/symlink realpath；33 测试；G4.3 trace 夹具待 R24 |
| packagejson | 4 | 662 | 单测绿 | — | Fields/Expected<T>/JSONValue/ExportsOrImports/Cache，STJ 流式容重复键；随 module |
| symlinks | 4 | 134 | 单测绿 | — | KnownSymlinks 双向缓存（SyncMap 按 Path 键）；8 Go 测试全移植（含线程安全→Parallel.For） |
| outputpaths | 4 | 307 | 单测绿 | — | emit 路径组合（GetOutputPathsFor/GetOutputExtension/GetCommonSourceDirectory）；经真实 SourceFile 的新单测；§1.5 复用 TsPath 类==命名空间尾 先例 |
| diagnosticwriter | 4 | 506 | 单测绿 | — | 全包移植完（DiagnosticWriter.cs 630 行：WriteFormatDiagnostic(s)/WithColorAndContext/WriteCodeSnippet 波浪线+gutter+`...`省略/flattenDiagnosticMessageChain 2 空格缩进/WriteLocation/related info/颜色非-tty 关闭）；解阻塞：补 Diagnostics.Format.cs(Localize/Format `{0}` GeneratedRegex/StringifyArgs) + Ast Diagnostic.Localize + scanner 3 个行列函数(GetECMALineAndUTF16CharacterOfPosition 等)；**errors.txt 字节承重**：行列 UTF-16 code unit、波浪线按 UTF-16Len/星面码点垫片、颜色仅 pretty 路径；12 单测（`file.ts(1,col): error TS####:` 逐字+UTF-16 列+链缩进+无色） |
| compiler | 4–6 | 5,658 | 移植中 | — | **前端管线打通**：FileLoader(ProcessAllProgramFiles/module.Resolver/import·typeref·lib 解析)+FilesParser(并行 WorkGroup/SyncMap 发现)+Host+Program(NewProgram=load→bind→pool，真实 BindSourceFiles)+CheckerPool 对接具体 Program；3 文件传递 import 图 smoke 证明加载→解析→模块解析→绑定；emit/诊断编排/CommonSourceDirectory 桩（阶段5/6）；修复 PackageId 零值去重坍缩 bug |
| checker | 5 | 59,822 | 移植中 | — | 22 partial 中 **16 个实质完成**：骨架 + 类型系统基础 + 类型操作 + relater(5 关系 ~5.8k 行) + inference(2k) + flow 窄化(~3.7k) + check* 分派 Statements(#5)/Expressions(#7)/OperatorExpressions(#8)(~3k)——checker 已能发类型错误 + **grammar 语法检查(grammarchecks.go 76 函数→Checker.Grammar.cs 2991 行，CheckGrammarSourceFile 入口门禁+全 checkGrammar* 族+grammarErrorOnNode 报告)**；~1.4k 方法；Deps dedup 模式（并行 agent 逐波收口至 0/0：relater/inference 84 错、flow/printer 64 错、check*/printer 114 错，grammar 自消 28 dup）；~90 测试 + 端到端探针（parse→bind→check→GetDiagnostics 已通，过 grammar 后卡下一桩 isCanceled）；待后续波：NameLookup/jsx/复杂声明校验长尾（class/interface/enum/module/import-export 全检、装饰器、iteration-types、签名/成员校验——均留 Deps 单桩）；遗留单桩：上述 + isCanceled/GetInferredTypeParameterConstraint/TypeToString/各簇 Deps 跨簇桩 |
| evaluator | 5 | 168 | 单测绿 | — | 常量折叠全量（前缀一元/二元数值全运算含 ** 右结合/字符串拼接/模板/字面量/实体委托）+ AnyToString/IsTruthy；17 测试经真实 parser+jsnum 验证 |
| nodebuilder | 5 | 78 | 单测绿 | — | ISymbolTracker 接口 + Flags/InternalFlags 枚举（printer emit resolver 用，阶段 6）；无单测视同达成 |
| pseudochecker | 5 | 1,126 | 未开始 | — | 由 .d.ts 基线间接验收 |
| modulespecifiers | 5 | 2,280 | 移植中 | — | 仅 ModuleSpecifierGenerationHost 接口 shell（checker 骨架最小建）；主体待阶段 5 后续 |
| printer | 6 | 11,442 | 移植中 | — | **基础设施 + factory/helpers + printer.go emit 主体 三波落地**（纯 AST 侧，与阶段 5 并行）：基础设施 EmitContext/EmitFlags/TextWriter/NameGenerator/EmitHost/EmitResolver(接口)等；factory.go→Factory.cs + helpers.go→Helpers.cs；**printer.go(~5k 行)→Printer.cs + Printer.{Emit,Expressions,Statements,Types,Jsx,Names,Support}.cs + PrinterUtilities(.Literals).cs（emit 主分派 pipelineEmit + 各 SyntaxKind emit*/emitList/缩进/trivia）**；EmitFlags 方法↔枚举经 EF 别名；§1.5 Scanner/ast 经 global::；PrinterTests 冒烟；**待后续波**：changetrackerwriter/utilities 尾 + 与 checker EmitResolver 实现接线（阶段 6 emit 门禁） |
| transformers | 6 | 24,323 | 未开始 | — | 7 个子命名空间；变换链顺序逐项保真 |
| sourcemap | 6 | 1,013 | 单测绿 | — | 全包移植完（Source/LineInfo/Util(Base64 VLQ)/Decoder/Generator/SourceMapper 6 文件，与阶段 5 并行落地）；列偏移 = UTF-16 code unit；RawSourceMap JSON 承重字节保真走 Utf8JsonWriter+UnsafeRelaxedJsonEscaping；GeneratorTests 全绿（VLQ 编码/addMapping/mappings 串逐字）；.js.map 全量字节门禁待阶段 6 emit 接线 |
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
| locale | 9 | 28 | 单测绿 | — | 微包提前（diagnosticwriter 依赖）：Locale.cs（`readonly struct Locale(Tag)` + Default==Go language.Und + TryParse）；**不用 CultureInfo/x-text-language**（InvariantGlobalization）；英文恒等（Default 无本地化消息，errors.txt 路径字节等同 Go locale.Default）；WithLocale/FromContext 按显式参数约定省略(PORT-NOTE) |

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
| 2026-07-17 | **★ 100.0%（2619/2619，零失败）★** | — | — | — | 83bc3ce3e；**errors.txt 全语料字节全等达成,failingCases.txt 清空**。收官链:declarations/transform.go 全 1:1 重建接线(2957L/109fns,批1-7b+接线,d9c09ad37…3c8648e70)→ GetDeclarationDiagnostics 真身+harness pre/post 双集接入(fb8cf7597,翻 declarationFiles/mappedTypeProperties/recursiveMappedTypes)→ export= TryGetValue+classExpressionAsTypeLiteral 过时 deferral 填补(4455f060a,清 23 崩溃)→ addPropertyToElementList accessor 双臂(83bc3ce3e,翻 mixinAccessors3)。AOT 冒烟过;**单线程模式(TSCS_SINGLE_THREADED=1)同样 2619/2619——确定性门禁通过,双模式字节全等**。⚠️ 教训:裸 `dotnet build TypeScript.slnx` 不刷新 Release 测试产物,门禁前必须显式 `-c Release` 构建测试项目(记忆 gate-build-discipline) |
| 2026-07-16 | **99.9%（2616/2619）** | — | — | — | b9e4f15b6 后跑批；**classfields.go 全港（3618 行/134 函数，6 批门禁验证零回归，commit 5d17ac0ec…51eefc512）+ SubtreeFacts 手写计算层补港**（ast.go 72 个手写 computeSubtreeFacts/propagateSubtreeFacts 从未被港——生成器只出 schema 派生体，致所有 SubtreeFacts 守卫的降级转换器在 emit 期于 SourceFile 守卫处直接 bail；今港至 Ast/Node.SubtreeFacts.cs）+ ast.ContainsObjectRestOrSpread/IsSuperProperty/IsInitializedProperty/GetClassExtendsHeritageElement + count-MATCH→postErrors（harnessutil.go:693 忠实）；`tscs --target es2015` 私名降级已与 tsc 字节等价（WeakMap/__classPrivateFieldGet）；AOT 冒烟过；余 3 案（declarationFiles/mappedTypeProperties/recursiveMappedTypes）全部收敛到 declarations/transform.go 单一单元（TS2615 需声明发射期重查位、TS2527 需 tracker+GetDeclarationDiagnostics） |
| 2026-07-13 | 26.5%（216/815） | — | — | — | 358a22ed3；CorpusRunner 首波（单文件、有 errors.txt 基线、harness 选项路由后的确定性 stride 采样 815 例；全语料 5,907 例待后续波）；**阶段 5 第一档（25%）达成**；另 curated 精选 16 例中 12 例字节匹配；失配 599 例入 failingCases.txt 允许清单（清单外失配=回归门禁） |
| 2026-07-14 | ~55.5%（≈452/815） | — | — | — | bf1f70ca9；簇驱动爬坡波 A–K 后同一 815 例语料复测（临时簇 fact 口径，非全量跑批签核）；此后波 L（e58abd297）/M（1736fa717）/N（2608cc96e）/O（50d6f3c30）续灭簇未复测；**全语料扩容 ~2,619 例**（2c5cbd5c3，EnumerateAllEligible 口径）+ TSCS_CONFORMANCE_UPDATE_FAILING 再生成跑批在途（bin 为 pre-L 构建，结果作 pre-L 基线，L–O 战果需新构建复跑收割）；misc-diff 定性归因：41 例纯输出 diff 收敛 9 模式（P1 harness tests/lib 挂载 11 例、P3/P6/P8 nodebuilder 打印面 ~16 例、P4/P9 拼写/lib 建议 6 例、P2 pre/post-emit 双跑 6 例、P5 TS6053 出口 2 例、P7 enum 字面量打印 2 例），离线驱动下 213 例已 MATCH（清单水位过时的直接证据） |
| 2026-07-16 | **96.7%（2532/2619）** | — | — | — | ecb8d3702 后跑批；**reportMergeSymbolError + parseBigIntLiteralType 全港**（checker.go:14127）——重复声明报告链 4 函数（reportMergeSymbolError/addDuplicateDeclarationErrorsForSymbols/addDuplicateDeclarationError/lookupOrIssueError + getAdjustedNodeForError：选 enum/块级/重复标识消息、跳过 plain-JS 侧、lookupOrIssueError 去重、"_0_ 也在此声明"/"及此处" related-info 链上限 5）+ parseBigIntLiteralType 一行去桩；96.6%→**96.7%（+1，零回归）**；翻绿 plainJSReservedStrict（templateLiteralTypes4 不再崩溃）；failingCases 87 条 |
| 2026-07-16 | **96.6%（2531/2619）** | — | — | — | 54af4d620 后跑批；**getModeForUsageLocation resolution-mode override 两分支**（fileloader.go:726）——type-only import/export attributes + import-type node attributes 的 `resolution-mode` 覆盖，经 AST 级 ImportAttributesNode.GetResolutionModeOverride()（nil-receiver→静态助手返 (mode,ok)）；import-type 分支原抛 NotImplementedException；96.6%→**96.6%（+1，零回归）**；翻绿 importTypeAmbientMissing；failingCases 88 条 |
| 2026-07-16 | **96.6%（2530/2619）** | — | — | — | e4758b7ab 后跑批；**getESDecoratorCallSignature 全港**（ES 装饰器上下文类型合成，原为桩）——`(target, context) => output \| void` 签名按被装饰元素（类/方法·getter·setter/字段·auto-accessor）合成 target/context/return 类型，落 15 助手（newESDecoratorCallSignature、newClass*DecoratorContextType×6、newClassAccessorDecoratorTarget/ResultType、newClassFieldDecoratorInitializerMutatorType、newGetter/SetterFunctionType、newClassMemberDecoratorContextTypeForNode、getClassMemberDecoratorContextOverrideType、newFunctionType、newProperty、tryCreateTypeReference），8 全局上下文解析器早已接线；96.6%→**96.6%（+1，零回归）**；翻绿 esDecorators-arguments；failingCases 89 条 |
| 2026-07-16 | **96.6%（2529/2619）** | — | — | — | d49fa1c53 后跑批；**fillMissingTypeArguments copy 越界修复**（checker.go:21860）——Go `copy(result, typeArguments)` 拷贝 min(len(result),len(typeArguments)) 项；C# 循环到 numTypeArguments，当 numTypeArguments>numTypeParameters（isJavaScriptImplicitAny 下 JS 全局接口增补带多余类型实参）越界写 result→IndexOutOfRange，下游状态污染表现为 typeResolutionHasProperty ArgumentNull；循环夹到 numTypeParameters；96.5%→**96.6%（+1，零回归）**；翻绿 checkJsdocSatisfiesTag12（duplicateNumericIndexers 不再崩溃但仍 diff）；failingCases 90 条；**本会话 7 修复收官 +39（95.1%→96.6%），余 90 例均为子系统级**（emit-harness 28/node-reuse 波/getSymbolChain/verifyCompilerOptions/lib 版本/深层逐例） |
| 2026-07-16 | **97.7%（2558/2619）** | — | — | — | 0ac79770c 后跑批；**module-reference 收集 + mergeModuleAugmentation 全港**（references.go + checker.go:1387）——将 CollectExternalModuleReferences no-op 换为忠实静态 collectExternalModuleReferences/collectModuleReferences（填充 file.Imports/ModuleAugmentations/AmbientModuleNames + node: 前缀 URI 检测，内联 IsAnyImportOrReExport/IsImportNode/GetExternalModuleName；dynamic-import 扫描延迟）+ checker mergeModuleAugmentation + InitializeChecker 两个合并循环；**填充 file.Imports（原空）广泛承重**；两次跑批稳定零回归 + AOT 洁净；97.4%→**97.7%（+7）**：ambientExternalModule/await+usingDeclarationsWithIteratorObject/checkJsxChildrenCanBeTupleType/controlFlowOptionalChain3/tsxSpreadAttributesResolution17/unicodeEscapesInJsxtags；failingCases 61 条 |
| 2026-07-16 | **99.2%（2598/2619）** | — | — | — | d9fa48c7a（会话续；11 commit，97.7%→**99.2%（+40，零回归，确定性）**）。**里程碑：并发 bug 修复 +4**（6813a7255）——collectCheckerDiagnosticsFromFiles 误走 per-file GetCheckerForFileNonExclusive（无锁）并发，同 checker（index%N）的多文件竞争 typeResolutions 栈致非确定性崩溃；改走 ForEachCheckerGroupDo（每 checker 一任务、持锁顺序访问其文件，program.go:583）→单==并行字节确定性门禁成立、真 bug 崩溃全清；翻绿 duplicateNumericIndexers/objectTypeHidingMembersOfExtendedObject/objectTypeWithStringIndexerHidingObjectIndexer/parserStrictMode8。**EMIT-HARNESS 桶解决 +28**（81b30a71b+d9fa48c7a）——env-gated 实验诊断出"~1010 回归"实为 Emit 崩于少数桩（GetConstantValue 202+printer utilities），非诊断失真；港 GetConstantValue（services.go:859）+6 printer utilities（isJSDocLikeText/IsPinnedComment/originalNodesHaveSameParent/calculateIndent/IsRecognizedTripleSlashComment+match-helpers/IsFileLevelUniqueName）→Emit 崩溃 266→1/分片；接线 harnessutil pre/post-emit 双跑（harnessutil.go:626）；两处字节等价偏差（count-MATCH 保 pre-emit 集避免 emit 侧 TS18017/18018 related-info 重排；Emit 不可完成时跳过双跑）；翻绿全部 emit-harness TS-1（YieldExpression*_es6/parser*/typeofThis*/thisTypeInFunctionsNegative/withStatements/symbolProperty53/54/classStaticBlock19/…）。**其余 +8**：plainJSErrors 允许清单 +3（program.go:2100）/grammar IsExpressionNode +1/真 SymbolToTypeNode ×3 站点（unique-symbol typeof+createAnonymousTypeNode，Symbol.toPrimitive 限定+þclass 泄漏）+2/newTypeMapper nil-slice NRE +1/bundled.WrapFS lib-sort +1/dynamic-import 扫描。**余 21**（皆子系统级，逐一读源已核）：modulespecifiers 包 2622 行（getSpecifierForModuleSymbol，最后 3 崩溃）、declaration transformer 2957 行~22%（GetDeclarationDiagnostics→declarationFiles TS2527）、emit-resolver related-info 排序（recursiveMappedTypes/mappedTypeProperties 需 post）、CommonJS module.exports/expando 建模、深层推断过报（TS7024 假环/上下文字面量收窄）、node-reuse visitor（未解析引用）、getSymbolChain 容器查找（mixinAccessors3/typeArgumentInference2）、parser-render 2 |
| 2026-07-16 | **99.9%（2616/2619）** | — | — | — | 23340c4b8（会话续-3；99.8%→**99.9%（+2，零回归，确定性）**）。**getNarrowedTypeOfSymbol 参数臂删除 strada 遗留自推断**（checker.go:13735 直取 nonFixingMapper;旧 clone+InferTypes 对正在解析的签名自身重入 getReturnTypeOfSignature → 假 TS7024）；翻绿 dependentDestructuredVariables+genericRestParameters1（同根因）。诊断套件：TSCS_CONFORMANCE_ONLY 过滤器+独立 tscs+临时环栈 dump+tsgo 交叉验证。**余 3 案全归声明发射子系统**：declarationFiles TS2527（declarations transformer tracker）、recursiveMappedTypes/mappedTypeProperties（emit-harness count-MATCH 保 pre 偏差,需 post 忠实）——checker 本体对 errors.txt 已尽,100% 需声明发射波 |
| 2026-07-16 | **99.8%（2614/2619）** | — | — | — | 5ad366a2a（会话续-3；99.7%→**99.8%（+4，零回归，确定性）**）。两大修复：**instantiateContextualType 真实现（checker.go:30574，原透传桩）** —— returnMapper 实例化让泛型调用实参中的上下文字面量保留字面量类型,翻绿 generatorReturnContextualType+unionAndIntersectionInference1；**typeReferenceToTypeNode 外层类型参数分组（3018-3047）** + appendReferenceToType/getAccessStack/createAccessExpression,实例化匿名类打印 `mixin.(Anonymous class)`,翻绿 mixinAccessors3+typeArgumentInferenceWithClassExpression2。另:CLI 挂上 bundled lib overlay(陈旧延迟补全,tscs 可独立诊断)+ TSCS_CONFORMANCE_ONLY 过滤器。余 5：dependentDestructuredVariables TS7024 假环、genericRestParameters1、recursiveMappedTypes 位置、declarationFiles TS2527、mappedTypeProperties |
| 2026-07-16 | **99.7%（2610/2619）** | — | — | — | 2678f5c3f（会话续-3；99.5%→**99.7%（+3，零回归，确定性）**）。**@type reparse 补全（reparser.go:369）** —— C# 的 ExpressionStatement 分支是陈旧 ast-gap 延迟（所引 GetAssignmentDeclarationKind 早已落地）；补上（@type 挂到 BinaryExpression.SetType → getWidenedTypeForAssignmentDeclaration 的 declaration.Type() 分支生效 → TS2322 受检）后整个 CommonJS/JSDoc-expando @type 簇翻绿：typeTagModuleExports+typeTagPrototypeAssignment+propertyAssignmentUseParentType2。余 9：上下文推断过报 4、getSymbolChain 限定 2、recursiveMappedTypes 位置、declarationFiles TS2527、mappedTypeProperties |
| 2026-07-16 | **99.5%（2607/2619）** | — | — | — | 7fb426d04（会话续-3；99.4%→**99.5%（+4，零回归，确定性）**）。**★ 阶段 5 第四档（99.5%）达成 ★** —— Any-带别名臂（nodebuilderimpl.go:3202-3205，8 行）一臂翻四案：未解析名保留书写形（getTypeFromTypeAliasReference 为每个未解析名造带书写名别名的 errorType；C# 叶分派曾在别名检查前把 Any 直送关键字臂）+ TypeAlias.ToTypeReferenceNode（3510）+ symbolToEntityNameNode（630）。翻绿 jsdocTemplateTag5+parserMissingLambdaOpenBrace1（T-vs-any）+parserRealSource6+prototypePropertyAssignmentMergedTypeReference。余 12：CommonJS/JSDoc-expando 3、上下文推断过报 4、getSymbolChain 限定 2、recursiveMappedTypes 位置、declarationFiles TS2527、mappedTypeProperties |
| 2026-07-16 | **99.4%（2603/2619）** | — | — | — | ae6227abc（会话续-3；99.3%→**99.4%（+2，零回归，确定性）**）。**里程碑：B11 伪类型子系统全港** —— pseudotypenodebuilder.go 全文件（765 行 → NodeBuilder.PseudoTypeNodeBuilder.cs，9 函数：pseudoTypeToNode(WithCheckerFallback) 全 20 种伪类型、pseudoTypeEquivalentToType 跨校验、pseudoParameters*、pseudoReturnTypeMatchesPredicate、pseudoTypeToType）+ 接线两个 JS 声明发射 reuse 消费点（serializeTypeForDeclaration tryReuse 2209-2255 + serializeReturnTypeForSignature tryReuse 2037-2059）+ NodeBuilder.pc 伪检查器实例 + ast.HasInferredType + hasTypeAnnotation + setCommentRange(473，并接 ObjectType.cs 两处 deferred 站点) + reuseTypeNode/reuseName/getTypeFromTypeNodeNb + rewriteModuleSpecifier 真实现（getModuleSpecifierOverride，被本会话 getSpecifierForModuleSymbol 解锁）+ IsExternalModuleSymbol + 全部 4 个 NodeBuilder 序列化入口（SerializeReturnType/TypeParameters/TypeForExpression/TryJSTypeNode + symbolToTypeParameterDeclarations 对）+ Program.GetSourceFileFromReference（program.go:231）+ getImportTypeNodeLiteral（**Parser 零桩**）+ getContainingNodeArray/siblingNodePositionsAreComparable + addDefaultValueAssignmentFor*（**Printer 零桩**）。**翻绿 assignmentCompatWithCallSignaturesWithRestParameters（可选参 x?: string 原样 reuse）+ jsdocTemplateTag（Array<any> 原样 reuse）** —— node-reuse 渲染簇首次被真正打穿。AOT 洁净。余 16 全为字节 diff：~4 CommonJS/JSDoc-expando、~2 JSDoc @template 类型参作用域（jsdocTemplateTag5/parserMissingLambdaOpenBrace1 的 T-vs-any）、~4 上下文推断过报、~2 getSymbolChain 限定、recursiveMappedTypes 位置、declarationFiles TS2527、genericRestParameters1、parserRealSource6 |
| 2026-07-16 | **99.3%（2601/2619）** | — | — | — | 613d6c0b7（会话续；~22 commit，99.2%→**99.3%（+3，零回归，确定性）**）。**里程碑：modulespecifiers 包全港 + 端到端接线，最后 3 个崩溃案清零**。港 types.go/util.go/preferences.go/specifiers.go 全部（GetModuleSpecifiers→GetModuleSpecifiersWithInfo→getAllModulePathsWorker+computeModuleSpecifiers→getLocalModuleSpecifier+tryGetModuleNameAsNodeModule+package.json exports/imports），并接线 nodeBuilder.getSpecifierForModuleSymbol（早分支 ambient/enclosingFile==nil→ast.GetSourceFileOfModule().FileName()；全管线 host=ch.program，因 Program:Host:ModuleSpecifierGenerationHost 无需适配器）。下游解阻塞港：**Program.GetSymlinkCache**（program.go:2022，lazy 符号链接缓存 从 resolutions+package.json 依赖，+ForEachResolvedModule/ForEachResolvedTypeReferenceDirective/forEachResolution，用已港 LazyValue<T>）、**reportNonExportedMember+reportInvalidImportEqualsExportMember**（checker.go:14835）、getFileSymbolIfFileSymbolExportEqualsContainer（symbolaccessibility.go:261）、canHaveModuleSpecifier+TryGetModuleSpecifierFromDeclaration+worker（nodebuilderimpl.go:1166/1187/1196）、ast.GetSourceFileOfModule/GetNonAugmentationDeclaration/IsExternalModuleAugmentation、Checker:CheckerShape（GetAliasedSymbol=ResolveAlias）、SourceFile:SourceFileForSpecifierGeneration、NodeBuilderSymbolLinks.specifierCache。翻绿 arbitraryModuleNamespaceIdentifiers_importEmpty（全管线）+ moduleExportsAliasLoop1/2（早分支）；AOT 洁净。**余 18 全为 errors.txt 字节 diff（零崩溃）**：~5 CommonJS/JSDoc-expando 检查建模（typeTagModuleExports 等，binder 已绑 module.exports 但 checker 未施 @type/查赋值）、~4 node-reuse 渲染（jsdocTemplateTag Array<any>-vs-any[]/assignmentCompatWithCallSignaturesWithRestParameters 可选参/parserMissingLambdaOpenBrace1 T-vs-any）、~4 上下文推断过报（generatorReturnContextualType/dependentDestructuredVariables TS7024 假环/unionAndIntersectionInference1/mappedTypeProperties）、~2 getSymbolChain 限定+多余类型实参（mixinAccessors3/typeArgumentInferenceWithClassExpression2）、recursiveMappedTypes 位置、declarationFiles TS2527、genericRestParameters1、parserRealSource6——皆子系统级/多会话；单修复胜局已尽 |
| 2026-07-16 | **97.4%（2551/2619）** | — | — | — | 0d9cec616 后跑批；**node-reuse 红利**：indexInfoToObjectComputedNamesOrSignatureDeclaration（借 reuseNode，computed-name 组件复用，+3：computedPropertyNamesContextualType8/9/10）+ instantiation-expression 类型复用（tryReuseExistingNonParameterTypeNode+typeNodeIsEquivalentToType，解桩 createAnonymousTypeNodeEx，+1：instantiationExpressionErrors）；97.3%→**97.4%（+4，零回归）**；failingCases 68 条 |
| 2026-07-16 | **97.3%（2547/2619）** | — | — | — | 1703e9267 后跑批；**node-reuse 子系统全港收官**（nodecopy.go:288）——完整 getExistingNodeTreeVisitor（visitExistingNodeTreeSymbolsWorker 全分派 + tryVisit* + 作用域管理外层 NodeVisitor 包装）+ reuseNode/tryReuseExistingNodeHelper + recovery-boundary/WrappingTracker + 实体名追踪；接线 typeToTypeNodeHelperWithPossibleReusableTypeNode（getTypeFromTypeNode(typeNode)==t 时复用源节点，bail→回退 typeToTypeNode 故零回归）；另解桩 Checker.IsSymbolAccessible→IsSymbolAccessibleWorkerCore（symbolaccessibility.go:839，复用 visitor 实体名可及性检查所需）；**类型引用保源写形式**（`T extends Array<Base>` 打印 `Array<Base>` 而非 `Base[]`）；两次跑批稳定零回归 + AOT 洁净；97.1%→**97.3%（+4）**：assignmentCompatWith{Call,Construct}Signatures3/4；failingCases 72 条 |
| 2026-07-16 | **97.1%（2543/2619）** | — | — | — | e19f51455 后跑批；**真实 symbolToTypeNode + createAccessFromSymbolChain 全港**（nodebuilderimpl.go:638）——趁 lookupSymbolChain/getSymbolChain 已实，将 typeReferenceToTypeNode 臂的 SymbolToTypeReferenceNodeLocal 近似替换为忠实 symbolToTypeNode（105行）+ createAccessFromSymbolChain（95行，限定名/索引访问/导出别名构建）+ lookupTypeParameterNodes（WriteTypeParametersInQualifiedName 分支延迟）+ getTopmostIndexedAccessType；Checker.SortSymbols 改 internal；getSpecifierForModuleSymbol 仍桩（仅外部模块 ImportType 臂）；**激活每个类型引用的真实限定符号名渲染**（N.A vs A），broad 渲染变更两次跑批零回归 + AOT 洁净；2542→**2543（+1）**：everyTypeWithAnnotationAndInvalidInitializer；failingCases 76 条 |
| 2026-07-16 | **97.1%（2542/2619）** | — | — | — | 444cc7cb2 后跑批；**getSymbolChain + sortByBestName 全港**（nodebuilderimpl.go:1081，原抛异常桩，曾标记「RISKY 延迟」）——替换 getSymbolChain 桩为真实限定符号链构建器（symbolToTypeNode/lookupSymbolChain 驱动）+ sortByBestName/sortedSymbolNamePair + getAliasForSymbolInContainer（symbolaccessibility.go:342）+ modulespecifiers.CountPathComponents；将 getAccessibleSymbolChain/needsQualification/getContainersOfSymbol/getQualifiedLeftMeaning/hasNonGlobalAugmentationExternalModuleSymbol 改 internal 供 nodebuilder 调用；slices.SortStableFunc→稳定 OrderBy；getSpecifierForModuleSymbol 仍桩（仅外部模块父触及，这些路径不触及）；**激活真实符号链限定路径**（原 5 例崩溃于桩），忠实港两次跑批零回归；96.9%→**97.1%（+5）**：assignFromStringInterface2/for-of29/indexSignatures1/symbolProperty21/usingDeclarationsWithObjectLiterals1；failingCases 77 条 |
| 2026-07-16 | **96.9%（2537/2619）** | — | — | — | 3554a4445 后跑批；**unused-locals/parameters/type-parameters 子系统全港**（checker.go:7050）——checkUnusedIdentifiers 编排器本已实，但其分派的 4 个 checkUnused* 叶（ClassMembers/LocalsAndParameters/InferTypeParameter/TypeParameters）为抛异常桩，且整个 reportUnused 报告族（reportUnused/Variable/Local/Variables/Parameters/BindingElements/VariableDeclarations/Imports + isUnreferencedVariableDeclaration/unusedIsError）+ isReferenced/isUnreferencedTypeParameter/isIdentifierThatStartsWithUnderscore/importClauseFromImported/allDeclarationsInSameSourceFile/rangeOfTypeParameters + UnusedKind 枚举全缺；忠实 1:1 港（报告/建议汇、symbolReferenceLinks、诊断消息皆已就绪）；2536→**2537（+1，零回归）**：翻绿 privateNameUnused，且消除 noUnusedLocals/noUnusedParameters 桩崩溃；failingCases 82 条 |
| 2026-07-16 | **96.8%（2536/2619）** | — | — | — | 72feaca51 后跑批；**清桩批**：getBooleanLiteralValue/getBigIntLiteralValue（checker.go:25262/25266 字面量值访问器）+ checkIfTypePredicateVariableIsDeclaredInBindingPattern（类型谓词绑定模式递归）——三个抛异常桩，各在其用例产出任何输出前崩溃；2534→**2536（+2，零回归）**：翻绿 templateLiteralTypes4、typeGuardFunctionErrors；failingCases 83 条；余下均为子系统级（node-reuse 依赖 SymbolTracker-wrapping 缓行波 + emit-harness 阻塞 + unused-checking ~300行 + 深层逐例崩溃：late-binding 双加/push-pop 失衡/NRE） |
| 2026-07-16 | **96.8%（2534/2619）** | — | — | — | af5ae0876 后跑批；**verifyCompilerOptions + verifyProjectReferences 全港**（program.go:729，原仅有尾部 emit-file-path 检查）——config-syntax 诊断闭包（createDiagnosticForOption/OptionName/RemovedOptionDiagnostic + compilerOptions 对象字面量定位）+「Removed in TS7」块（baseUrl/outFile/target=ES5/module=AMD,System,UMD/moduleResolution=Classic,node10/alwaysStrict=false/esModuleInterop=false/allowSyntheticDefaultImports=false/downlevelIteration）+ 全部一致性检查（strict*/isolated*/inlineSourceMap/composite/paths/sourceMap/declaration*/lib+noLib/isolatedModules/outDir/checkJs/emit*/decorator/jsx*/module-resolution）；补 verifyProjectReferences（ConfigFile==null 空引用路径）+ hasZeroOrOneAsteriskCharacter/moduleResolutionSupportsPackageJsonExportsAndImports/emitModuleKindIsNonNodeESM 助手 + ModuleKind.ToDisplayString stringer（原抛异常桩，兼解 checker JSON-import-attribute 诊断）；禁用反射 JsonSerializer 换 Utf8JsonWriter；96.5%→**96.8%（+6，两次跑批稳定零回归）**：destructuring*ES5iterable（downlevelIteration TS5102）+ ModuleKind stringer 解阻塞 4 例；failingCases 85 条 |
| 2026-07-16 | **96.5%（2528/2619）** | — | — | — | eb1d2175d 后跑批；**reportTypeNotIterableError 返回诊断**（checker.go:6683）——errorAndMaybeSuggestAwait 本已返回 *ast.Diagnostic，故 reportTypeNotIterableError 可返回之（原 PORT-NOTE 称返 null 待 statements 波已过时）；现 getIterationTypesOfIterableSlow 收集的 TS2489「迭代器须有 next() 方法」related-info 经 AddRelatedInfo 真正挂到 TS2488 错误，而非丢在 null 诊断上；96.4%→**96.5%（+2，零回归）**；翻绿 for-of16、iteratorSpreadInArray10；failingCases 91 条；余下主体：TS-1 emit-harness 28（阻塞于 emit 管线）、crash/stub 24（node-reuse getSymbolChain/indexInfoToObjectComputedNames/单例崩溃）、node-reuse SAME-count ~11（Array<>/可选参 \|undefined，需 nodecopy.go ~900 行）、verifyCompilerOptions 缺失（downlevelIteration 等 removed-option，Go 495 行）、IteratorObject lib 版本（全局 Iterator 作值） |
| 2026-07-16 | **96.4%（2526/2619）** | — | — | — | 7a94d3e01 后跑批；**harness 修复：直接应用 harness-extra 编译选项**（harnessutil.go:293）——tsgo SetOptionsFromTestConfig 经 getCommandLineOption+ParseCompilerOptions 应用 @ 指令选项（不走 CLI-flag 串解析），故认得 OptionsDeclarations 之外的 4 选项（allowNonTsExtensions/noErrorTruncation/suppressOutputPathCheck/noCheck）；本 runner 原将全部指令走 ParseCommandLine→`--suppressoutputpathcheck` 报 TS5023、值 `true` 当根文件→TS6231+related；改为映射小写指令键→规范名直接落到已解析 CompilerOptions；96.2%→**96.4%（+7，零回归）**；翻绿 checkJsdocOptionalParamOrder/checkJsdocTypeTag1/2/checkJsdocTypeTagOnObjectProperty1/2/checkJsdocTypedefOnlySourceFile/jsdocParamTagTypeLiteral；failingCases 93 条 |
| 2026-07-16 | **96.2%（2519/2619）** | — | — | — | becaa7ae1 后跑批；**getInferredTypeParameterConstraint 全港**（checker.go:17027，原为无约束桩）——`infer T` 声明的推断约束：Foo<infer T>（取 Foo 类型参约束实例化）/rest·named-tuple（unknown[]）/模板字面量 span（string）/mapped 参（string\|number\|symbol）/反向 mapped 条件；缺此约束时 `${infer U}` 模板占位符与变长-rest 占位符对 templateConstraintType/unknown[] 的 checkTypeAssignableTo 假失败→伪报 TS2322/2344/2574；96.0%→**96.2%（+6，零回归）**；翻绿 templateLiteralTypes1/2/3、inferTypes1、variadicTuples1、typeParameterConstModifiers；failingCases 100 条 |
| 2026-07-16 | **96.0%（2513/2619）** | — | — | — | c359d7a44 后跑批；**Iterable 家族默认类型实参省略**——typeReferenceToTypeNode 对 Iterable/IterableIterator/AsyncIterable/AsyncIterableIterator（各新增 TReturn=any,TNext=any 两参）省略与默认相同的尾随类型实参→渲染 `Iterable<Foo>` 而非 `Iterable<Foo, any, any>`（nodebuilderimpl.go:3056 省略循环 + 补 3 个 GetGlobal*Type 包装）；95.7%→**96.0%（+6，零回归）**；翻绿 generatorTypeCheck25/31/62/63 + types.asyncGenerators/forAwait.es2018.2；failingCases 106 条；**离线全量 diff 归因固化**（testdata/baselines/local 实际输出 vs reference）：TS-1 emit-harness 28（最大簇，阻塞于 emit 管线）、node-reuse SAME-count ~11（Array<> 约束/可选参 \|undefined/computed-index-sig，均经 serializeTypeForDeclaration 复用源节点——nodecopy.go ~900 行为下一渲染杠杆）、nodeBuilder 桩 ~10（getSymbolChain/getSpecifierForModuleSymbol/indexInfoToObjectComputedNames）、模板字面量/变长元组约束过报（templateLiteralTypes1 +112 等，深层 relater 单根因）、单例崩溃 2、under-report 3 |
| 2026-07-16 | **95.7%（2507/2619）** | — | — | — | 3b324e24c 后跑批；**P7 enum 字面量打印修复**——`typeToString`（Checker.SymbolTypes.cs）港时私加 StringLiteral/NumberLiteral/BooleanLiteral/Intrinsic 快路径，抢在 nodebuilder 之前返回裸字面量值；enum-literal 类型带 NumberLiteral/StringLiteral 标志，单成员 enum 的声明类型（E.A）被拦截渲染为 `0` 而非经 EnumLike 臂渲染枚举名 `E`（Go printer.go:177 typeToString 无快路径，直委 typeToStringEx）；删快路径后 95.1%→**95.7%（+17，零回归）**；diff 簇 88；failingCases 112 条；下簇 Top：diff 88、nodeBuilder 10、InvalidOperation 3、ArgumentOutOfRange 2、getESDecoratorCallSignature 1、parseBigIntLiteralType 1 |
| 2026-07-15 | **95.1%（2490/2619）** | — | — | — | 后跑批；checkModuleAugmentationElement + checkPrivateIdentifierExpression；95.0%→**95.1%**；failingCases 129 条；**checker 移植阶段实质收官**——余下 129 例中 104 为纯 diff（无异常），需 emit 双跑 harness（tests/ 基架，~21）+ Array<X> 打印 + 模板字面量过报等；剩余 checker stub 仅 nodeBuilder 10（对象序列化边角）+ isValidBigIntString 2 等零星 |
| 2026-07-15 | **95.0%（2489/2619）** | — | — | — | 后跑批；**装饰器子系统（legacy 路径）全港**（checkDecorator/resolveDecorator/getLegacyDecoratorCallSignature + 5 辅助，ES 路径 getESDecoratorCallSignature 延迟）；94.8%→**95.0%（+7）**；**本会话第五个子系统，破 95%**；failingCases 130 条；余下 diff 簇 101 例为压倒性主体（需 emit 双跑 harness），stub 剩 nodeBuilder 10、checkModuleAugmentationElement 4、checkPrivateIdentifierExpression 2、isValidBigIntString 2 |
| 2026-07-15 | **94.8%（2482/2619）** | — | — | — | 后跑批；checkImportCallExpression（动态 import()，依赖经模块解析子系统已就绪）；94.6%→**94.8%**；failingCases 137 条；余下 diff 簇 101 例为压倒性主体（emit 双跑 harness ~21 + 打印细节），stub 剩 nodeBuilder 10、checkDecorator 8（装饰器子系统 ~350 行 ES/legacy 签名合成，最深剩余单元） |
| 2026-07-15 | **94.6%（2478/2619）** | — | — | — | 后跑批；hasAsyncModifier + parameterInitializerContainsUndefined + isSomeSymbolAssigned 三小件；94.3%→**94.6%**；failingCases 141 条；余下以 **diff 簇 101 例**为主体（emit pre/post 双跑 harness ~21 + Array<X> 约束打印 + 模板字面量过报），stub 剩 checkDecorator 8（装饰器子系统）/checkImportCallExpression 4/nodeBuilder 10 |
| 2026-07-15 | **94.3%（2471/2619）** | — | — | — | 后跑批；**模块解析 synthetic-default 子系统全港**（resolveESModuleSymbol/getExternalModuleMember + canHaveSyntheticDefault/getTypeWithSyntheticDefault*/cloneTypeAsModuleType 等 ~14 函数，esModuleInterop）；93.9%→**94.3%（+11）**；**本会话三大子系统全部落地**（可及性/LS 网关/模块解析）；failingCases 148 条；下簇 Top：diff 99、nodeBuilder 10、checkDecorator 8、checkImportCallExpression 4、hasAsyncModifier 3、parameterInitializerContainsUndefined 3 |
| 2026-07-15 | **93.9%（2460/2619）** | — | — | — | 后跑批；getContextualTypeForAwaitOperand + typePredicateToString（经新 NodeBuilder.TypePredicateToTypePredicateNode 入口）；93.7%→**93.9%**；failingCases 159 条；下簇 Top：diff 99、nodeBuilder 9、checkDecorator 8、resolveESModuleSymbol 8、getExternalModuleMember 5、checkImportCallExpression 4 |
| 2026-07-15 | **93.7%（2455/2619）** | — | — | — | 后跑批；**LS 网关子系统全港**（getSymbolAtLocation/getTypeOfNode/getSymbolOfNameOrPropertyAccessExpression + 8 ast 谓词，checker.go:31338-31795）——里程碑 2 LS 功能（hover/definition/completions/fourslash）的 checker 查询面就绪；93.6%→**93.7%**；failingCases 164 条；下簇 Top：diff 99、nodeBuilder 9、checkDecorator 8、resolveESModuleSymbol 8、getExternalModuleMember 5、checkImportCallExpression 4、getContextualTypeForAwaitOperand 4 |
| 2026-07-15 | **93.6%（2452/2619）** | — | — | — | 后跑批；getContextualTypeForStaticPropertyDeclaration + LookupSymbolForPrivateIdentifierDeclaration nil-map 守卫（ANE 簇 6 例）；93.2%→**93.6%**；failingCases 167 条；下簇 Top：diff 99、nodeBuilder 9、checkDecorator 8、resolveESModuleSymbol 8、getExternalModuleMember 5、checkImportCallExpression 4、getSymbolAtLocation 4 |
| 2026-07-15 | **93.2%（2441/2619）** | — | — | — | 后跑批；padTupleType + nodebuilder unique-ES-symbol 臂（typeof x，借新落地的 IsValueSymbolAccessible）；92.9%→**93.2%**；diff 簇 103→99；failingCases 178 条；下簇 Top：diff 99、nodeBuilder 9、ANE 9、checkDecorator 8、resolveESModuleSymbol 8、getExternalModuleMember 5、getContextualTypeForStaticPropertyDeclaration 5 |
| 2026-07-15 | **92.9%（2432/2619）** | — | — | — | 后跑批；**符号可及性子系统全港**（symbolaccessibility.go，~15 函数，symbolTableID→SymbolTable 引用同一性偏差）+ nodebuilder 类型别名名称渲染臂（nodebuilderimpl.go:3332，`T1`/`ReactNode`/`U` 按别名名渲染而非展开结构）；90.0%→**92.9%（+75）**；diff 簇 173→103，零回归；failingCases 187 条；下簇 Top：diff 103、nodeBuilder 9、checkDecorator 8、resolveESModuleSymbol 8、ANE 8、getExternalModuleMember 5、padTupleType 5 |
| 2026-07-15 | **90.0%（2357/2619）** | — | — | — | 后跑批；padObjectLiteralType + getPropertyNameFromBindingElement；89.9%→**90.0%**；**★ 阶段 5 第三档（90%）达成 ★**；failingCases 262 条；下簇 Top：diff 173、nodeBuilder 10、ANE 9、checkDecorator 8、resolveESModuleSymbol 8；向 99.5% 第四档爬坡（剩余以 diff 簇定性归因 + 装饰器/模块解析深支 + emit 基线接入为主）|
| 2026-07-15 | **89.9%（2355/2619）** | — | — | — | 后跑批；checkConstEnumAccess + getIsolatedModulesLikeFlagName + isValidNumberString；89.7%→**89.9%**；failingCases 264 条；**距 90% 仅 3 例**；下簇 Top：diff 173、nodeBuilder 10、ANE 9、checkDecorator 8、resolveESModuleSymbol 8、checkExportSpecifier 5、padObjectLiteralType 5 |
| 2026-07-15 | **89.7%（2348/2619）** | — | — | — | 后跑批；getContextualTypeForBindingElement + checkJSDocType/checkJSDocTypeIsInJsFile + late-binding 批（trackComputedName/cloneBindingName）；89.1%→**89.7%**（+15）；failingCases 271 条；**距 90% 仅 0.3pp**；下簇 Top：diff 167、ANE 10、nodeBuilder 10、checkConstEnumAccess 8、checkDecorator 8、resolveESModuleSymbol 8、isValidNumberString 7 |
| 2026-07-15 | **89.1%（2333/2619）** | — | — | — | 后跑批；typeToTypeNode conditional 臂 + typeToTypeNodeOrCircularityElision；**全 6 个渲染臂完成**（无类型再落 any leaf）；89.0%→**89.1%**；failingCases 286 条；距 90% 仅 0.9pp；剩 nodeBuilder 簇 15（addPropertyToElementList/cloneBindingName/indexInfoTo…）+ 小 stub checkConstEnumAccess/getContextualTypeForBindingElement/checkDecorator/resolveESModuleSymbol 各 8 |
| 2026-07-15 | **89.0%（2332/2619）** | — | — | — | 后跑批；typeToTypeNode string-mapping（Uppercase<X>）+ substitution（NoInfer<X>/base）臂；88.9%→**89.0%**（破 89%）；failingCases 287 条；仅剩 Conditional 臂落 leaf 回退；距 90% 仅 1.0pp |
| 2026-07-15 | **88.9%（2329/2619）** | — | — | — | 后跑批；typeToTypeNode 渲染臂 index(keyof)/indexed-access(T[K])/template-literal（nodebuilderimpl.go:3442-3488）；mapped-type 模板从 `any` 转为 `T[K]` 正确渲染；88.7%→**88.9%**（+6 净转绿）；diff 169→163；failingCases 290 条；StringMapping/Conditional/Substitution 臂仍延迟（需 symbolToTypeNode/conditionalTypeToTypeNode）；距 90% 仅 1.1pp |
| 2026-07-15 | **88.7%（2323/2619）** | — | — | — | 后跑批；createMappedTypeNodeFromType（nodebuilderimpl.go:1452，mapped-type 节点合成全主干 + 2 个 homomorphic 助手）；匹配数持平但 **nodeBuilder 异常簇 24→15**（9 例 mapped-type 从抛异常前移到字节 diff：现渲染 `{ [K in keyof T]: any; }`，待 T[K] indexed-access 模板渲染方可字节匹配）；diff 161→169；失败链推进，非净转绿 |
| 2026-07-15 | **88.7%（2323/2619）** | — | — | — | 后跑批；isPrimitiveOrObjectOrEmptyType + getSuggestedTypeForNonexistentStringLiteralType + substituteIndexedMappedType 三小件；88.4%→**88.7%**；failingCases 296 条；失败链前移（diff 145→161、nodeBuilder 18→24）；下簇 Top：diff 161、nodeBuilder 24、checkJSDocType 11、checkConstEnumAccess 8、getContextualTypeForBindingElement 8、checkDecorator 8、resolveESModuleSymbol 8；距 90% 仅 1.3pp |
| 2026-07-15 | **88.4%（2316/2619）** | — | — | — | 后跑批；getApplicableIndexSymbol（synthetic index property symbol，checker.go:31839，11 例）；88.0%→**88.4%**；failingCases 303 条；下簇 Top：diff 145（需第三轮归因）、nodeBuilder 18、isPrimitiveOrObjectOrEmptyType 12、getSuggestedTypeForNonexistentStringLiteralType 12、checkJSDocType 11、substituteIndexedMappedType 10；距第三档 90% 仅 1.6pp |
| 2026-07-15 | **88.0%（2305/2619）** | — | — | — | 后跑批；flowLoopStack nil-slice 修复（GetTypeAtFlowLoopLabel NRE，Go nil 切片 range/append 语义→C# 空 List，19 例）；87.4%→**88.0%**；failingCases 314 条；下簇 Top：diff 145、nodeBuilder 18、getSuggestedTypeForNonexistentStringLiteralType 12、checkJSDocType 11、getApplicableIndexSymbol 11、substituteIndexedMappedType 10、isPrimitiveOrObjectOrEmptyType 10 |
| 2026-07-15 | **87.4%（2289/2619）** | — | — | — | 后跑批；checkImportAttributes 全家族（+getTypeFromImportAttributes/getResolutionModeOverride/getEmitSyntaxForModuleSpecifierExpression/ast.IsExclusivelyTypeOnlyImportOrExport，20 例）；86.7%→**87.4%**；failingCases 330 条；下簇 Top：diff 143、NRE 24、nodeBuilder 18、getSuggestedTypeForNonexistentStringLiteralType 12、checkJSDocType 11、getApplicableIndexSymbol 11、ArgumentNullException 9 |
| 2026-07-15 | **86.7%（2271/2619）** | — | — | — | 后跑批；signatureToString/signatureToStringEx（printer.go:309，经 NodeBuilder.SignatureToSignatureDeclaration 新公开入口，26 例）；86.0%→**86.7%**；failingCases 348 条；下簇 Top：diff 143、NRE 23、checkImportAttributes 20、nodeBuilder 18、getSuggestedTypeForNonexistentStringLiteralType 12、checkJSDocType 11、getApplicableIndexSymbol 11 |
| 2026-07-15 | **86.0%（2253/2619）** | — | — | — | 298d748d7 后跑批；checkDestructuringAssignment 全家族 6 函数（object/array/reference 解构赋值，顶簇 31 例）；84.9%→**86.0%**；failingCases 366 条；下簇 Top：diff 138、signatureToString 26、NRE 22、checkImportAttributes 19、nodeBuilder 18、getSuggestedTypeForNonexistentStringLiteralType 12、checkJSDocType 11、getApplicableIndexSymbol 11 |
| 2026-07-15 | **84.6%（2216/2619）** | — | — | — | 0704660ac 后跑批；主循环亲自移植波（子代理用量上限撞死，转主循环）：getGlobal* 解遮蔽 10+getImmediateAliasedSymbol+小簇批 5+newConditionalType(48 顶簇)+shorthand/qualified/generator+yield/nonUndefined/uncalledFn+discriminate 对象成员+isValidOverrideOf；80.4%→**84.6%**；failingCases 403 条；下簇 Top：diff 136、checkDestructuringAssignment 31、NRE 23、checkImportAttributes 19、nodeBuilder 18、signatureToString 15、getDeprecatedSuggestionNode 11、checkJSDocType 11、getApplicableIndexSymbol 11 |
| 2026-07-15 | **80.4%（2105/2619）** | — | — | — | 17ef0843d 后跑批；getGlobal* 解遮蔽（10 个已接线 resolver）+ getImmediateAliasedSymbol（c9ab78b54，30 例簇）+ 小簇批 IsKnownSymbol/getTypePredicateParent/hasExportedMembersOfKind/checkRegularExpressionLiteral/resolveInstanceofExpression（17ef0843d）；75.9%→**80.4%**（+4.5pp，破 80）；failingCases 514 条；下簇 Top：newConditionalType 48（在途）、checkShorthandPropertyAssignment 25、checkDestructuringAssignment 24、discriminateContextualTypeByObjectMembers 24、checkImportAttributes 19、checkQualifiedName 17、checkGeneratorInstantiation 17、getContextualTypeForYieldOperand 16、diff 116 |
| 2026-07-15 | **75.9%（1987/2619）** | — | — | — | 65bc9ee26 后跑批；累计收割波 T（issueMemberSpecificError 类成员域）/U（checkTaggedTemplateExpression+checkAsyncFunctionReturnType）/V（拼写建议子系统 TS2552/2551/2583 + featureMap）/ambient-module 合并（5ba3f8934，tsx 簇字节翻绿）/P5 include-processor（a809bd815，TS6053/TS5055，parserRealSource 系列翻绿）+ ast 微件；68.2%→**75.9%**（+7.7pp）；failingCases 632 条；diff 簇 138→93；下簇 Top：newConditionalType 31、getImmediateAliasedSymbol 30、checkRegularExpressionLiteral 29、IsKnownSymbol 27、checkShorthandPropertyAssignment 23、getTypePredicateParent 23、checkDestructuringAssignment 23、discriminateContextualTypeByObjectMembers 22、resolveInstanceofExpression 21、hasExportedMembersOfKind 21 |
| 2026-07-14 | **68.2%（1785/2619）** | — | — | — | e72e0fcd1 后跑批；波 S 三小件打包（containsArgumentsReference/isNotOverload/extractDefinitelyFalsyTypes 三簇 122 出表）+ AreTypeParametersIdentical NRE 修复（合并接口）收割 +98；failingCases 834 条；diff 簇 119→138（异常例转纯 diff，第二轮归因在途）；下簇 Top：issueMemberSpecificError 34、isInstancePropertyWithInitializerOrPrivateIdentifierProperty 32（两件波 T 在途）、checkTaggedTemplateExpression 31、checkAsyncFunctionReturnType 30、getFirstNonAmbientClassOrFunctionDeclaration 30、newConditionalType 30 |
| 2026-07-14 | **64.4%（1687/2619）** | — | — | — | b1fe30ea7 后跑批；波 R checkSuperExpression 族 + entityNameToString 族（两簇 48+37 出表）+ ast.* 微件簇（03166e90e，45 例）合计 +63；failingCases 932 条；下簇 Top：diff 119（需第二轮定性归因）、containsArgumentsReference 44、isNotOverload 40、extractDefinitelyFalsyTypes 38（三件波 S 在途）、issueMemberSpecificError 34、isInstancePropertyWithInitializerOrPrivateIdentifierProperty 32、checkTaggedTemplateExpression 31、checkAsyncFunctionReturnType 30、newConditionalType 30 |
| 2026-07-14 | 62.0%（1624/2619） | — | — | — | 0577d9d8b；**阶段 5 第二档（60%）达成**——波 Q nodebuilder 打印面（3af0fc5dc：union 成员回流 builder 核/this-type 臂/实参切片剔 this-arg/tuple 臂全量/getNameOfSymbolAsWritten）+ P1 harness tests/lib 挂载（84953eb90 后续提交）+ type-only import 别名链（b10038fbc）合计 +69 例；diff 簇 147→115；failingCases 995 条；后续 03166e90e 又灭 ast.* 微件簇 45 例（跑批后落地，未计入本行）；下簇 Top：checkSuperExpression 48（波 R 在途）、isNotOverload 39、containsArgumentsReference 39、extractDefinitelyFalsyTypes 38、entityNameToString 37（波 R 在途）、issueMemberSpecificError 34 |
| 2026-07-14 | 59.4%（1555/2619） | — | — | — | e8baf0d6f；**首次全合格语料跑批**（EnumerateAllEligible 全量 2,619 例，非采样；bin=波 P `7a6be7246` + 波 Q 部分在途），4 分片（TSCS_CONFORMANCE_SHARD 2×2 并行）+ 逐例 journal 合并；failingCases.txt 再生成 1,064 条；**阶段 5 第二档（60%）差 0.6pp**；新簇表 Top：diff 147（misc-diff 归因的打印/harness 面）、nodeBuilder 桩 56（波 Q 在途）、checkSuperExpression 48、containsArgumentsReference 41、extractDefinitelyFalsyTypes 38、entityNameToString 37、isNotOverload 36、issueMemberSpecificError 34、checkTaggedTemplateExpression 31、checkAsyncFunctionReturnType 30；教训固化：单进程 2.5h 跑批卡死全损 → 分片+journal 进 harness（`fix(harness)` a451107c8：vstest 下 theory 折叠/输出被吞，改用 xUnit v3 原生 exe） |

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
| D-007 | 2026-07-11 | core/LinkStore.cs | plan/03 §5.2 的 `ReferenceEqualityComparer.Instance`（BCL，`IEqualityComparer<object?>`，接口不变型）无法充当 `IEqualityComparer<K>`——改用自研泛型 `Ts.Core.ReferenceEqualityComparer<T> where T:class` | 编译期（语义等价；非基线） | plan 片段不可编译，03 §5.2 / 05 §7.5 应改名此泛型包装（并影响 checker HashSet<Type> 站点）| 生效 |
| D-008 | 2026-07-11 | ast/Symbol.cs | InternalSymbolName.Prefix 保留 Go 的 `"\xFE"`，在 UTF-16 下是 U+00FE(þ)——合法 JS 标识符字符（Go 侧是非法 UTF-8 字节 0xFE），理论上与 þ 前缀用户标识符碰撞 | 语义（极罕见碰撞；非基线） | 按"原样"移植；建议主计划评估改用真正非标识符码元 U+FFFE 以求 UTF-16 保真 | 生效 |
| D-005 | 2026-07-11 | vfs/Internal/Common.cs | DecodeBytes 无 BOM 路径对**无效 UTF-8** 的替换粒度：Go 按 rune 迭代逐无效字节产一个 U+FFFD，.NET Encoding.UTF8 按最大无效子序列产一个 U+FFFD；有效 UTF-8 双方逐字符等价 | 行为（仅无效 UTF-8 源文件；conformance 语料待核） | C# string=UTF-16 边界（plan/01），BCL 解码器语义；AGENTS §6 登记备查 | 生效 |
| D-006 | 2026-07-11 | vfs/FileProvider.cs | IFileProvider 失败以 null 表达、不携错误明细；WalkDir 算法在 stat/ReadDir 失败时给回调传 `VfsUtil.ErrNotExist` 哨兵而非 Go 的 \*PathError | 行为（walkFn 的 err 身份；tsgo 生产 walkFn 均只判空/吞掉，唯 fourslash 基架 errors.Is(fs.ErrNotExist)——阶段 9 前重估） | plan/05 §4 就地消费判据；survey 证据见 vfs 移植会话 | 生效 |
| D-009 | 2026-07-13 | fswatch/（FsWatch/ 全包） | 平台后端（inotify/fanotify/kqueue/FSEvents/RDCW+syscall）整包坍缩为单一 FileSystemWatcher；rename→delete+create 经 EventList 合并；overflow→InternalBufferOverflow 映射 ErrOverflow；递归 watch 合并启发式（consolidation threshold=10）不移植（FSW subtree 原生廉价） | 行为（事件合并/丢事件语义与 Go 后端不同；公共 API 契约逐一对应；非基线) | §8.2 预批准第 3 条转正（06 §8.5、12-risks）；42 单测含真实 FSW 冒烟 | 生效 |
| D-010 | 2026-07-13 | tracing/（Tracing/ 全包） | 事件发射经 Ts-Tracing EventSource；trace 输出 .nettrace（非 .pb.gz）；Chrome-trace/legend/types.json 写出器保字节忠实（Utf8JsonWriter+UnsafeRelaxedJsonEscaping）；shell 接口（ITracer/ITracedType/Phase/NewTypeTracer）签名原样 | 输出格式（观测设施；非基线） | §8.2 预批准第 2 条转正（06 §9.2）；9 单测 | 生效 |
| D-011 | 2026-07-13 | pprof/（Profiling/ 全包） | 剖析 API 面→.NET diagnostics；字节捕获为可注入 IProfileCapture 后（EventPipeProfileCapture 现写占位 .nettrace 标记，未引入 Microsoft.Diagnostics.NETCore.Client 保 AOT 自包含）；CPUProfiler.StopCPUProfile→Try 模式保 Go 错误文本 | 输出格式（观测设施；非基线） | §8.2 预批准第 1 条转正（06 §7.2） | 生效 |

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
| 2026-07-14 | v1.1 | Claude Code 会话（簇驱动爬坡 H–O + 语料扩容 + misc-diff 归因） | **§5 第二行落笔：815 例 26.5%→~55.5%**（波 A–K，bf1f70ca9 及之前 4 commit：H instantiateTypeWithSingleGenericCallSignature+checkExternalImportOrExportDeclaration、I checkImportBinding+IsSignatureApplicable NRE、J resolveExternalModuleName 主干+ResolveUntypedCall NRE、K getIntersectedSignatures+GetPragmaFromSourceFile+flow nil-slice）；续四波零间隙轮转：**L**（e58abd297）checkDeferredNode switch 与 checker.go:2491-2526 全等 + require/type-only 别名链 10 桩；**M**（1736fa717）getSpreadType 家族 8 函数 + createUnionOrIntersectionProperty 全主干（灭 2 桩零新桩）；**N**（2608cc96e）resolveUnion/IntersectionTypeMembers+getUnionSignatures+判别收窄链（灭 7 桩零新桩）；**O**（50d6f3c30）mapped-type 域 20 函数（resolveMappedTypeMembers/instantiateMappedType 全家族+流收窄叶子，灭 14 桩零新桩）；**波 P 在途**（checkTypeParametersNotReferenced lib 检查最浅阻塞帧+getStringMappingType 域）；**语料扩容**（2c5cbd5c3 [Codex]）815→~2,619 合格例新 API + 全语料再生成跑批在途（bin 为 pre-L，作 pre-L 基线）；**fourslash parser 半场落地**（680805c54 [Codex]，5 测绿），runner 半场 + ls symbols/folding/diagnostics 三件套两条 Codex 车道在途；**misc-diff 定性归因完成**：579 失败例全量离线复跑（非抽样），41 diff 例收敛 9 模式（P1 harness tests/lib 挂载 11 例最高性价比、P3/P6/P8+tuple 序列化打包 nodebuilder 波 ~28 例、P4/P9 建议桩 6 例、P2 pre/post-emit 双跑候补 emit 波、P7 enum 字面量打印带精确 repro），213 例离线已 MATCH（清单过时证据）；新异常簇 AreTypeParametersIdentical NRE×6（合并接口）/GetTypeAtFlowLoopLabel NRE×2/LookupSymbolForPrivateIdentifierDeclaration ANE×1 登记待波 |
| 2026-07-13 | v1.1 | Claude Code 会话（conformance 26.5% + LSP + awaited + ls 深化） | **§5 历史表首行落笔**：CorpusRunner 语料 16→815 例、字节匹配 216/815=**26.5%**（**阶段5 第一档 25% 达成**）+ failingCases.txt 628 例允许清单（清单外失配=回归门禁；xUnit theory 显示名 `···` 截断经基线目录前缀匹配补全）；port(checker)：#22 awaited-types 全链 17 函数（**TS2349 探针转正**：TS2349+TS2757 链端到端）+ 诊断报告核心（Error/GetSymbol/OnFailedToResolveSymbol/AddErrorOrSuggestion）+ curated 收官波（CheckBaseTypeAccessibility/CheckObjectTypeForDuplicateDeclarations/CheckKindsOfPropertyMemberOverrides 等，**curated 9→12**）+ compareProperties/checkInheritedPropertiesAreIdentical；忠实性认定：NoLib 下 `n()` 无文件诊断为**忠实行为**（globalFunctionType 为空对象→Go 同走 untyped-call 静默；探针 fixture 改内联全局接口，对照真实 tsgo 验证）；port(ls)：findAllReferences+signatureHelp+completions；port(lsp)：协议子集（STJ 源生成+OR 转换器）+Server（initialize/didOpen/hover/definition/completion/publishDiagnostics）+**tscs --lsp 接线**（Codex）；偏差 D-009~D-011 转正（fswatch/tracing/pprof）；反复中断 agent 处置模式固化（先锁增量入库再续跑/上下文过长即退役重派）；commits 34f56070c/7e9d588ce/d414a6aaa/f8c732fd0/358a22ed3，全绿 3005 通过/0 失败 |
| 2026-07-12 | v1.1 | Claude Code 会话（tsoptions tsconfig 加载） | port(tsoptions)：tsconfigparsing.go 加载切片 → TsConfigParsing.{Load,Worker}.cs（同 partial class）：JSON-source→object 走查(convertConfigFileToObject/convertToJson/convertPropertyValueToJson/convert{Object,Array}LiteralExpressionToJson/jsonConversionNotifier/parseOwnConfigOf{Json,JsonSourceFile}) + parseConfig(循环检测/extends 合并 applyExtendedConfig) + parseJsonConfigFileContentWorker(getPropFromRaw/files-empty/默认 include/validateSpecs/${configDir}/references) + getFileNamesFromConfigSpecs(vfsmatch.ReadDirectory + hasFileWithHigherPriorityExtension/removeWildcardFilesWithLowerPriorityExtension) + getExtendedConfig/ParseExtendedConfig/getExtendsConfigPath{,OrArray}(§1.5 global::Ts.Module.Module.ResolveConfig，ResolverHost 垫片) + handleOptionConfigDirTemplateSubstitution + GetParsedCommandLineOfConfigFile{,Path}/readJsonConfigFile；删 TsConfigParsing 3 桩(ParseConfigFileTextToJson/ParseJsonConfigFileContent/GetParsedCommandLineOfConfigFile)；ParsedCommandLine 实现 outputpaths.OutputPathsHost + 补 CommonSourceDirectory/GetOutputFileNames(yield 迭代器)/GetBuildInfoFileName/PossiblyMatchesFileName(glob.Parse try/catch)/ParseInputOutputNames/ReloadFileNamesOfParsedCommandLine/WildcardDirectoryGlobs/checkSourceFilesBelongToPath，删 6 桩；port(ast)：AstUtilities.IsComputedNonLiteralName 补 Utilities.Flow.cs(tsoptions JSON 走查用)；StringifyJson 承重路径以 per-caller stringifyStringArrayJson 精确 emitter 替代(AOT 延后一致)；No-inputs/files-empty/File_0_not_found/Cannot_read_file 诊断齐；新 TsConfigParsingTests.cs 15 测(compilerOptions 绝对化/files 字面/include-exclude 通配/默认 include/单层 extends 合并+覆盖/references 绝对化/GetOutputFileNames/CSD 尾斜杠/configDir 替换/JSON 走查/非对象根报错) → 隔离 worktree(HEAD 5a9e33ed5)build 0/0、TsOptions 59 测全绿、Ast/OutputPaths/Module 149 测无回归；唯余 locale.Parse 桩(phase 9)；tsoptions 移植中→单测绿。**共享程序集因并行 checker/transformers 在途而红——本会话仅在隔离 HEAD worktree 验证自身文件 0/0；未提交(任务指令)** |
| 2026-07-12 | v1.1 | Claude Code 会话（8 路并行批 commit 4c49b17e1） | 8 disjoint 并行波一批入库（108 文件 +19,467 行，build 0/0、2690 passed/29 skipped、AOT 兼容）：**transformers**(infra Transformer/Chain/visitor/factory-adapters + jsxtransforms/moduletransforms/inliners + estransforms ES 降级；estransforms smoke 测 Skip-gate WIP)；**CLI**(Ts.Cli/Execute tsc 类型检查报告 + Compiler/Program.Diagnostics 语义/语法/bind 聚合 + Ts.Cli.Tests)；**LanguageService**(jsonrpc 帧 + format 引擎 135 规则表/RulesMap/FormattingScanner；FormatSpanWorker 后补)；**fswatch**(FileSystemWatcher 重写，42 测)；**tracing+pprof**(EventSource/EventPipe 重写，shell 接口稳，9 测)；**pseudochecker**(.d.ts 伪检查器，Codex 失败版经手工收口)；**checker e2e 里程碑**：getBaseTypeOfLiteralType/isCanceled/produceDeferredDiagnostics 落地——探针对 `const x:number="hello"` 全链跑通(grammar→decorators→赋值兼容→关系失败→报错)仅卡 typeToStringEx；收口 3 个过时 checker 测(union 解析/instantiation 守卫返 errorType)+CLI NoLib 作用域测+pseudochecker 残错。多 Codex 任务(transformers/estransforms/pseudochecker/format)接入(pseudochecker 失败经手工救) | port(checker)：grammarchecks.go(2202，76 函数)→Checker.Grammar.cs(2991 行，CheckGrammarSourceFile 入口门禁+全 checkGrammar* 族)，删 13 CheckGrammar* 桩；port(diagnosticwriter)：diagnosticwriter.go(506)→src/Ts.Compiler/DiagnosticWriter/（errors.txt 字节承重格式化器：行列 UTF-16/波浪线/链缩进/related/颜色非-tty 关）+ 解阻塞补 Diagnostics.Format.cs(Localize/Format/StringifyArgs) + Ast Diagnostic.Localize + scanner 3 行列函数；port(locale)：微包 Locale.cs（英文恒等，禁 CultureInfo）；checker(diagnostics)：GetDiagnostics/GetGlobalDiagnostics 实（checker.go:13875）；新端到端探针 CheckerEndToEndTests（parse→bind→check→GetDiagnostics 已通，过 grammar 后卡 isCanceled 桩，[Fact(Skip)] 追踪）；2 并行 agent（grammar/diagnosticwriter）落地，grammar 自消 28 dup、diagnosticwriter 全程 §1.5 隔离零碰撞 → build 0/0、test 2603 passed/19 skipped、AOT publish OK；checker 15→16 partial；diagnosticwriter/locale 未开始→单测绿 |  port(checker)：check* 分派三簇 Checker.Statements.cs(#5，2182-7016，checkSourceElement 全分派+语句/变量/函数/类型别名，1527 行) + Checker.Expressions.cs(#7，7494-12292) + Checker.OperatorExpressions.cs(#8，12293-13874)（表达式/运算符核心）+ 各 Deps——**checker 首次能发类型错误**；port(printer)：printer.go(~5k)→Printer.cs+8 分部(Emit/Expressions/Statements/Types/Jsx/Names/Support)+PrinterUtilities(.Literals)+NameGenerator；3 并行 agent（2 checker check* + 1 printer.go）落地后 114 编译错（89 可空+25 结构）经收口 agent 忠实清零（§1.5 CoreUtil.IfElse/OrElse/Some、EvaluatorFactory.IsTruthy、Scanner.ComputeLineOfPosition、Set 用 Ts.Collections、缺失谓词 GetContainingFunction/IsInstantiatedModule 补移 ast、可空按 Go 语义）→ build 0/0、test 2591 passed/18 skipped、AOT publish clean；checker 12→15 partial；printer emit 主体落地；未提交前工作树验证全绿 |  port(checker)：flow.go(2734)→Checker.Flow.{cs,Support.cs,Deps.cs}（getFlowTypeOfReference(Ex)/getTypeAtFlowNode/narrowType* 全族 + 21 跨簇桩）；port(ast)：Ast/Utilities.Flow.cs 补移 5 谓词（IsFunctionOrModuleBlock/IsVarConstLike/TryGetTextOfPropertyName/IsThisInTypeQuery/IsEmptyArrayLiteral）+ checker/utilities 补 4 谓词（HasDotDotDotToken/HasOnlyExpressionInitializer/IsCallChain/GetBindingElementPropertyName）；port(printer)：factory.go(1315)→Factory.cs + helpers.go(558)→Helpers.cs + PrinterStubs.cs；2 并行 agent（flow/printer）socket 中断后落地 64 编译错，经收口 agent 忠实清零（§1.5 重定位 EvaluatorFactory.AnyToString/AstUtilities.IsAccessExpression&IsClassLike/InternalSymbolName.Prefix、AsLiteralExpression 去伪、可空按 Go 语义）→ build 0/0、test 2578 passed（+9 FlowTests）/17 skipped；checker 11→12 partial；printer factory/helpers 落地；未提交前工作树验证全绿 |  port(checker)：relater.go(5006)→Checker.Relater.{cs,Engine.cs,Signatures.cs,Support.cs,Deps.cs}（5 关系引擎/结构化比较/relationCount 预算）+ inference.go(1651)→Checker.Inference.{cs,Support.cs,Deps.cs}（inferTypes/InferenceContext/优先级）；port(printer)：emit 基础设施地基波 src/Ts.Compiler/Printer/\*（EmitContext/EmitFlags/TextWriter/NameGenerator/EmitResolver 接口等 9 文件，纯 AST 侧）；port(sourcemap)：全包 src/Ts.Compiler/SourceMap/\*（Base64 VLQ/Generator/Decoder/SourceMapper 6 文件）+ GeneratorTests；4 并行 agent 落地后 84 编译错误经 Codex 子代理统一收口（mapper 静态限定/静态-实例/NodeTests→AstUtilities/§1.5/Set API/可空）→ build 0/0、test 2569 passed 17 skipped；checker 9→11 partial；printer 未开始→移植中；sourcemap 未开始→单测绿；快照两指标重算（编译通过≥20 行、当前阶段句）；未提交前的工作树验证全绿 |
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

7. **core 包宿主静态类 `Core` vs `CoreUtil` 冲突（04 vs 05）**：plan/05 §1.5（命名权威，第 169 行）钦定包级函数落**与包同名的 `Core`**（并举例 binarysearch.go→`partial class Core`）；plan/04 §2.2 #10（第 167 行）却写 `core.SameMap`→`Ts.Core.CoreUtil.SameMap`，且 generate-csharp-ast.ts 已据此发射 `CoreUtil.SameMap`。实况：CoreUtil 已成事实标准（生成器 + vfs + core.go + scanner 共 ~582 处引用），仅 5 个早期文件（Pattern/TextChange/NodeModules/ProjectReference/CompilerOptions/BinarySearch）用 `Core`。二者共存、编译通过、全测试绿（cosmetic 非功能问题）。**建议裁定统一到 `CoreUtil`**（最小 churn：改 5 个 Core 文件 + 修 05 §1.5，避免动 582 处生成物与在飞 scanner），待主计划确认后统一执行；在此之前两者并存不阻塞。
8. **阶段 4 内含阶段 5/9 依赖（Program agent 发现）**：主计划把 compiler.Program（签名含具体 `*checker.Checker`）与 diagnosticwriter（需 `Localize/Format`+locale）划入阶段 4，但二者硬依赖阶段 5 checker 类型与阶段 9 locale，无法在阶段 4 内接口占位完成。**裁定采用的落地序**：阶段 5 checker 骨架（types 24 类 + Checker 壳 + NewChecker + checkerpool 具体）先行 → 再 compiler.Program/fileloader + smoke → diagnosticwriter 随阶段 9（或补最小英文 Localize 提前）。建议主计划 09-phases 记此序。
