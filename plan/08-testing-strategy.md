# 08 — 测试策略（Testing Strategy）

> 版本：v1.1 ｜ 日期：2026-07-11 ｜ 状态：Draft
>
> 本文是移植工程的**验收机器说明书**：定义六类测试的职责边界、dumptool 夹具规格、差分测试框架、conformance 跑批器移植、两处 UTF 兼容垫片、tsc/tsbuild 基线、fourslash 策略、AOT 双轨 CI 与性能基准。
>
> 交叉引用：[00-overview.md]（上游钉扎与只读纪律）、[01-architecture.md]（UTF-16 决策、LargeStackExecutor、WorkGroup、JSON 字节保真）、[04-codegen.md]（生成器族与再生成命令）、[05-conventions.md]（Go→C# 惯用法、map 迭代序审计）、[06-module-inventory.md]（测试资产包清单）、[07-aot-constraints.md]（AOT 禁令与 analyzer 门禁）、[09-phases.md]（各阶段门禁定义）、[11-progress.md]（通过率历史登记）、[12-risks.md]（风险册）。
>
> 所有 Go 源码引用基于钉扎的 tsgo 工作区 `/Users/token/Desktop/code/typescript-go-main`（下文简写 `tsgo/`），行号以该 commit 为准；升级 pin 时本文引用需随 [00-overview.md] 的漂移处置流程复核。

---

## 1. 测试分类学总览

### 1.1 验收哲学

主计划（§四）确立了唯一验收货币：**与 tsgo 输出的逐字节 diff**。一切测试形态都是这个货币的不同兑换窗口，按阶段先后依次接管：

```
阶段 1        阶段 2-4              阶段 5-7                 阶段 8            贯穿
移植单测  →  差分夹具测试      →   conformance 基线      →  fourslash    +   AOT 复跑 / 性能趋势
(函数级)     (token/AST/symbol)    (errors/types/js/map)     (LSP 行为)       (发布产物 / 趋势)
```

### 1.2 六类测试

| # | 类别 | Oracle（判定源） | 主要阶段 | 承载项目 | 门禁角色 |
|---|------|----------------|---------|---------|---------|
| A | **移植单测** | Go `*_test.go` 中的断言，逐用例直译 | 1 起贯穿 | `tests/Ts.Compiler.Tests`、`tests/Ts.LanguageService.Tests` | 阶段 1 出口（core/collections/tspath/vfs/jsnum/stringutil 单测全绿，主计划 §二） |
| B | **差分夹具测试** | dumptool（tsgo 补丁分支）导出的 token 流 / AST 结构 / 解析诊断 / symbol / flow 夹具 | 2–4（阶段 5 后降级为回归护栏） | `tests/Ts.Compiler.Tests`（`Differential` 命名空间） | 阶段 2 出口（token 流 100%）、阶段 3 出口（AST + 解析诊断 100%）、阶段 4 出口（symbol/flow 100%） |
| C | **conformance 基线** | `testdata/baselines/reference/` 下从 tsgo 坐标 A 同步的 49,321 个 Git 跟踪文件 | 5–7 | `tests/Ts.Conformance.Tests` | 阶段 5（errors→types/symbols 分级至 100% 字节匹配）、阶段 6（js/d.ts/map 100%）、阶段 7（tsc/tsbuild/tsbuildinfo） |
| D | **fourslash** | convertFourslash 生成的用例断言 + `testdata/baselines/reference/fourslash/` 基线 | 8 | `tests/Ts.Fourslash.Tests` | 阶段 8 分级 50%→90%→99%+ |
| E | **AOT 复跑** | 与 C 类相同的基线，但被测对象是 `dotnet publish /p:PublishAot=true` 产物 | 各阶段门禁 + main/nightly | `tests/Ts.Conformance.AotHost`（见 §8.4） | 主计划 §四.2：日常 JIT、门禁 AOT 复跑 |
| F | **性能基准** | 无判定 oracle；对照 tsgo 本机构建，只做趋势 | 10（基础设施阶段 7 起可用） | `tests/Ts.Benchmarks` | 不设硬门禁（阶段 7 的 ≤2× tsgo 是独立 e2e 脚本测的门禁，不属本类，见 §9.5） |

补充两条贯穿性检验（主计划 §四.3）：
- **确定性检验**：同一套 C 类跑批在 `--singleThreaded`（WorkGroup 串行）与默认 4-checker 并行两种模式下输出必须全等；实现为 conformance 跑批器的一个开关维度（§4.2）。
- **金丝雀语料**：深嵌套表达式/条件类型文件常备于 B 类语料（[01-architecture.md] §4.6 验证钩子），JIT 与 AOT 双轨都跑，专门暴露栈深度问题。

### 1.3 测试项目布局

```
tests/
  Ts.TestUtil/                # Go internal/testutil(5,730 LOC) + testrunner(889 LOC) 的移植宿主
    Baseline/                 #   baseline.go/testmain.go → BaselineRunner.cs / BaselineTracker.cs
    Diff/                     #   github.com/peter-evans/patience 的 C# 移植（§4.6，.diff 字节保真所需）
    Harness/                  #   harnessutil.go → CompileFiles/TestFile/HarnessOptions/SourceMapRecorder…
    TsBaseline/               #   tsbaseline 6 个写出器（§4.5、§5、§6）
    TestCaseParser.cs         #   test_case_parser.go
    RepoPaths.cs              #   internal/repo 的路径定位助手
  Ts.Compiler.Tests/          # A 类（编译器侧移植单测）+ B 类（Differential/ 子命名空间）
  Ts.Conformance.Tests/       # C 类（xUnit 入口，JIT 轨）
  Ts.Conformance.AotHost/     # E 类（无 xUnit 的控制台宿主，AOT 发布，§8.4）
  Ts.Fourslash.Tests/         # D 类（Gen/ 生成 + Manual/ 手写）
  Ts.LanguageService.Tests/   # A 类（ls/format/lsp/project 侧单测 + LSP 回放）
  Ts.Benchmarks/              # F 类（BenchmarkDotNet）
testdata/
  baselines/reference/        # 从 tsgo 同步，只读（[00-overview.md] 只读纪律）
  baselines/local/            # 跑批器写出的实际输出，.gitignore
  submoduleAccepted.txt       # 与 tsgo 同步（§4.7）
  submoduleTriaged.txt        # 与 tsgo 同步（§4.7）
  tests/cases/{compiler,conformance}/   # tsgo 本地新增语料（tsgo 实测 297 个 .ts）
fixtures/                     # 自有夹具（仓库根，裁决 R14）：B 类分片压缩包 *.tar.zst + jsnum/ + fixtures.lock.json（§2.5、§2.7）
                              #   自有物移出 testdata/ ⇒ testdata/ 保持 tsgo 纯镜像（rsync -a --delete 无需 exclude）
_submodules/TypeScript/       # 与 tsgo 钉扎同 commit 的官方仓库 submodule
tools/
  dumptool/                   # §2：tsgo 补丁分支的补丁集 + 再生成脚本
  baseline-accept/            # §4.8：hereby baseline-accept 的 C# 等价
  fourslash-update-failing/   # §7.4：updateFailing.mts 等价
  e2e-compare/                # §8.5 / §9：Ts.Cli vs tsgo 真实项目对比脚本
```

测试框架：xUnit v3（主计划 §一.3）。测试项目不做 AOT 发布，但 `Ts.TestUtil` 与 `Ts.Conformance.AotHost` 必须通过 AOT/Trim analyzer（AotHost 要被发布；TestUtil 被 AotHost 引用）——此项对 [07-aot-constraints.md] 的「测试基架」政策行有一个补充要求，已列入 §11 待澄清第 3 条。

---

## 2. dumptool 规格（差分夹具的 Go 侧出口）

### 2.1 定位与实现方式：tsgo 补丁分支

**目的**：在阶段 2–4（scanner/parser/binder），C# 侧还没有能力产出 conformance 基线，需要一个比基线更细粒度的 oracle。dumptool 从 tsgo 内部状态导出 token 流、AST 结构、解析诊断、symbol 表、flow 图五族夹具；C# CI 只消费导出产物，**不需要 Go 工具链**（主计划 §二导语）。

**实现方式**：dumptool 必须能访问 tsgo 的 `internal/` 包（`internal/scanner`、`internal/parser`、`internal/binder`、`internal/ast`），而 Go 的 internal 可见性规则决定了它**只能活在 tsgo 模块树内部**。因此 dumptool 是「对 tsgo 的补丁分支」：

- `tools/dumptool/patches/` 存放一组 `git format-patch` 产物，应用在 [00-overview.md] 钉扎的 tsgo commit 上；
- **补丁纪律 P1：只增文件，不改既有文件**。dumptool 全部代码位于新增的 `cmd/dumptool/` 与 `internal/dumptool/` 两个目录。tsgo 现有导出面已足够：`scanner.Scanner` 的 `Scan/ReScan*/Mark/Rewind/Token/TokenStart/TokenEnd/TokenFlags/TokenText/TokenValue`（tsgo/internal/scanner/scanner.go:229-344, 469, 1013-1367）、`parser.ParseSourceFile`、`binder.BindSourceFile`（tsgo/internal/binder/binder.go:96）、`ast.ComputePositionMap`（tsgo/internal/ast/positionmap.go:40）全部公开，无需侵入改动。若未来某族夹具（如 checker 中间态）确需钩子，须以最小 diff 修改并把该 patch 单独成文件、在 `patches/README.md` 登记理由；
- 只增不改 ⇒ 升级上游 pin 时 rebase 近乎零成本（[00-overview.md]:241 升级流程第 5 步「rebase dumptool 补丁分支并重导出全部差分夹具」）。

### 2.2 命令行设计

```
dumptool
  -corpus <dir>[,<dir>...]   # 语料根目录，递归枚举 .ts/.tsx（默认：_submodules/TypeScript/tests/cases/compiler,
                             #   _submodules/TypeScript/tests/cases/conformance, testdata/tests/cases）
  -out <dir>                 # 夹具输出根目录
  -families <list>           # tokens,rescan,ast,parsediag,symbols,flow（默认全部）
  -shard <i>/<n>             # 分片再生成：按语料相对路径的 xxh3_64 % n == i-1 过滤（并行机器分工用）
  -verify                    # 不写出，改为与 -out 现有内容比对，不一致列清单退出码 1（防夹具腐化）
  -list                      # 只打印将处理的语料清单（调试）
```

固定行为（不做成开关，保证两侧协议唯一）：
- 每个语料文件按**原始全文**处理，不做 `// @filename` 多文件拆分（理由见 §3.2）；
- `.tsx` 用 `core.LanguageVariantJSX`，其余 Standard；ScriptTarget 取 Latest；解析选项用 parser 默认 + 按扩展名推断 ScriptKind；
- symbol/flow 族对解析产物执行 `binder.BindSourceFile` 后导出；
- 遇 panic：捕获并写出 `<name>.panic.txt`（含 stack），继续下一个文件——夹具生成必须能一次跑完全语料。

### 2.3 输出格式规范（normative）

#### 2.3.1 通用约定

1. **编码与换行**：UTF-8、无 BOM、LF（`\n`）行尾、文件末尾恰一个 LF。夹具是我方自有格式，不受 conformance 基线 CRLF 惯例（§4.5）约束，取最简。
2. **位置一律是 UTF-16 code unit 偏移**（见 §2.4）。
3. **字段分隔**：逗号。可变文本字段永远放行尾，转义后不含裸换行，因此无需转义逗号以外的分隔冲突（text 内逗号原样保留，解析器按「前 N-1 个逗号」切分）。
4. **text 转义规则**（两侧共用同一实现，C# 侧放 `Ts.TestUtil/Differential/FixtureEscape.cs`）：
   - `\` → `\\`；U+000A → `\n`；U+000D → `\r`；U+0009 → `\t`；
   - 其余 < U+0020 的控制符、U+2028、U+2029 → `\u{XXXX}`（小写 hex，无前导零填充）；
   - 其他一切码点按 UTF-8 原样直写（中文、emoji、星面字符不转义）；
   - Go 侧输入若含无效 UTF-8 序列：每个无效字节替换为 U+FFFD 后再转义（与 Go `strings.ToValidUTF8` 语义一致；C# 侧文本天然是 UTF-16，不会出现该分支，出现 lone surrogate 时同样打成 U+FFFD——两侧在畸形输入上约定一致）。
5. **kind/flags 的呈现**：kind 用枚举名字符串（Go `ast.Kind.String()`，tsgo/internal/ast/kind_stringer_generated.go；C# 用 [04-codegen.md] 的 stringer 生成物 `ToDisplayString()`）。名字比数字稳健：两侧枚举都由同一份 ast.json 生成，但名字在 diff 报告里可读。flags 用 `0x` 前缀大写 hex。
6. **文件头**：每个夹具文件首行为 `# dumptool v<协议版本> family=<族> file=<语料相对路径>`；协议版本变更必须全量重导出并更新 fixtures.lock.json（§2.6）。

#### 2.3.2 token 流：`<相对路径>.tokens.txt`

驱动协议：新建 Scanner，`SetText(全文)`，`.tsx` 时 `SetLanguageVariant(JSX)`，循环 `Scan()` 直到 `EndOfFile`（含 EndOfFile 行）。每个 token 一行：

```
kind,pos,end,flags,text[,value]
```

- `pos` = `TokenStart()`（跳过前导 trivia 后的起点），`end` = `TokenEnd()`，均经 §2.4 预转 UTF-16；
- `flags` = `TokenFlags()`（tsgo/internal/ast/tokenflags.go:3-30）；
- `text` = `TokenText()` 经 §2.3.1(4) 转义；
- `value` 字段仅当 `TokenValue() != TokenText()` 时追加（标识符 unicode 转义、字符串/模板转义解码、数字规范化等场景）——这是对主计划「kind,pos,end,flags,text」五元组的**显式扩展**：TokenValue 承载 scanner 的转义解码逻辑（`\u{}` 大码点→代理对、数字分隔符剥除等），是高价值差分面。

示例：

```
# dumptool v1 family=tokens file=compiler/unicodeEscapesInNames02.ts
ConstKeyword,0,5,0x0,const
Identifier,6,9,0x800,\u{61}bc,abc
EqualsToken,10,11,0x0,=
...
EndOfFile,120,120,0x1,
```

#### 2.3.3 rescan 探针流：`<相对路径>.rescan.txt`

裸 `Scan()` 循环覆盖不到上下文相关词法（正则 vs 除号、JSX 文本、模板续段、`>>` 合并等）——这些由 parser 按语法上下文调 `ReScan*` 触发。为了让阶段 2（无 parser）就能验证这些路径，规定一张**确定性探针表**：主循环每返回一个 token，若其 kind 命中探针表，则 `state := Mark()` → 执行探针 → 记录一行 → `Rewind(state)` 恢复主流（Mark/Rewind：tsgo/internal/scanner/scanner.go:284-288）。

| 触发 kind（主流刚返回） | 探针调用 | 备注 |
|---|---|---|
| `SlashToken`、`SlashEqualsToken` | `ReScanSlashToken()`（scanner.go:1067） | 正则字面量路径：对每个 `/` 无条件探测，覆盖 regexp.go 全部标志/类扫描 |
| `GreaterThanToken` | `ReScanGreaterThanToken()`（:1021） | `>>`/`>>>`/`>=` 合并 |
| `LessThanLessThanToken` | `ReScanLessThanToken()`（:1013) | `<<` 拆回 `<` |
| `CloseBraceToken` | `ReScanTemplateToken(false)`（:1052） | 模板中段/尾段 |
| `AsteriskEqualsToken` | `ReScanAsteriskEqualsToken()`（:1058） | `*=` 拆回 `*` |
| `HashToken` | `ReScanHashToken()`（:1232） | 私有标识符 |
| `QuestionToken` | `ReScanQuestionToken()`（:1240） | `??` 拆分 |
| （仅 .tsx）`GreaterThanToken`、`CloseBraceToken` | `ReScanJsxToken(true)`（:1225） | JSX 文本扫描 |
| （仅 .tsx）`Identifier` | `ScanJsxIdentifier()`（:1321） | JSX 连字符标识符 |

行格式：`probe,kind,pos,end,flags,text[,value]`，`probe` = 上表探针名（如 `ReScanSlashToken`）。**此表是 normative**：C# 侧 `TokenFixtureComparer` 必须实现同一张表、同一顺序；表的任何修改都是协议版本升级。探针在 Mark/Rewind 保护下不影响主流，两族夹具可独立 diff。JSDoc 扫描 API（`ScanJSDocToken` 等，:1374-1418）不设探针——其入口在 parser 的 JSDoc 解析中，由 AST 族覆盖。

#### 2.3.4 AST 结构：`<相对路径>.ast.txt`

`parser.ParseSourceFile` 后，从 SourceFile 起按 `ForEachChild` 顺序先序遍历，每节点一行，2 空格/层缩进：

```
Kind [pos,end) flags=0xF
```

- `pos/end` = `node.Loc.Pos()/.End()`（tsgo 中含前导 trivia 的 fullstart 语义，两侧一致），经 §2.4 预转 UTF-16；
- `flags` = `node.Flags`；
- 首行注释后附 `# text-utf16-len=<N>`（全文 UTF-16 长度），作为 PositionMap 自检锚点。

节点先序序号（0 起，含根 SourceFile）被 symbol/flow 族用作 `nodeIndex` 引用坐标，故 **AST 族与 symbol/flow 族必须同批生成**。

示例：

```
# dumptool v1 family=ast file=compiler/a.ts
# text-utf16-len=25
SourceFile [0,25) flags=0x0
  VariableStatement [0,24) flags=0x0
    VariableDeclarationList [0,23) flags=0x2
      VariableDeclaration [6,23) flags=0x0
        Identifier [6,9) flags=0x0
        ...
  EndOfFile [24,25) flags=0x0
```

#### 2.3.5 解析诊断：`<相对路径>.parsediag.txt`

阶段 3 门禁的「解析诊断 (code,pos,len) 三元组 100%」（主计划 §二）。对 `sourceFile.Diagnostics()` 按原序每条一行：

```
code,pos,len
```

`pos` 预转 UTF-16；`len` 是**UTF-16 长度**（= UTF16(pos+len_bytes) − UTF16(pos)，不是字节长直转）。不含消息文本（消息由诊断表生成器保证，[04-codegen.md]），不排序（顺序本身是 parser 行为的一部分）。

#### 2.3.6 symbol 转储：`<相对路径>.symbols.txt`

`BindSourceFile` 后两段式输出：

**第一段：声明映射。** 先序遍历 AST，对每个 `node.DeclarationData() != nil && Symbol != nil` 的节点输出：

```
decl,nodeIndex,symId,name,symFlags
```

- `symId`：**夹具局部 id**，按首次遇见顺序从 0 递增分配（不用 tsgo 的 `ast.Symbol.id`——那是 atomic 全局计数，进程相关不可复现，tsgo/internal/ast/symbol.go:18）；
- `name` = `Symbol.Name` 转义（含 `__missing`、`"quoted module"` 等内部名，注意 InternalSymbolName 前缀 `\xFE` 是有效待转义控制外字符——按 §2.3.1(4) 直写 UTF-8）；
- `symFlags` = `Symbol.Flags`。

**第二段：locals 表。** 对每个 `node.LocalsContainerData() != nil && len(Locals) > 0` 的节点输出：

```
locals,nodeIndex,name1=symId1;name2=symId2;...
```

SymbolTable 是 Go 无序 map ⇒ **键按 Unicode 码点序排序**。注意：这不是 C# `string.CompareOrdinal`（UTF-16 unit 序，星面字符会排在 U+E000..U+FFFF 之前），而 Go 的字符串字节比较对有效 UTF-8 恰为码点序。C# 侧必须使用专门的 `CodePointComparer`（`Ts.TestUtil/Differential/CodePointComparer.cs`）——这正是 [05-conventions.md] ordinal 审计规则的一个实例，登记 [12-risks.md]。
Symbol 的 `Members/Exports` 同法以 `members,nodeIndex,...` / `exports,nodeIndex,...` 行输出在声明节点之后。

#### 2.3.7 flow 转储：`<相对路径>.flow.txt`

先序遍历 AST，对每个 `node.FlowNodeData() != nil && FlowNode != nil` 的节点，从该 FlowNode 出发沿 `Antecedent`/`Antecedents` 链 DFS（tsgo/internal/ast/flow.go:27-35：`FlowNode{Flags, Node, Antecedent, Antecedents}`）。每个 FlowNode 按**首次发现顺序**编号，每节点一行：

```
flow,flowId,flowFlags,nodeIndex|-,antecedents=[id;id;...]
```

- `nodeIndex`：`FlowNode.Node` 对应 AST 节点的先序序号；无关联节点写 `-`；
- 已编号节点再次到达只在 antecedents 列表引用，不重复展开（图有环——循环流）；
- 另输出锚点行 `anchor,nodeIndex,flowId` 记录「AST 节点 → 其 FlowNode」映射。

### 2.4 位置预转 UTF-16 的实现点

Go 侧一切 `pos/end` 是 UTF-8 字节偏移；C# 侧（删除 PositionMap 后，[01-architecture.md] §UTF-16 决策）一切位置天然是 UTF-16 code unit。**转换发生且只发生在 dumptool 写出的一处**：

- 每个语料文件构建一次 `pm := ast.ComputePositionMap(text)`（tsgo/internal/ast/positionmap.go:40-60），所有写出的偏移经 `pm.UTF8ToUTF16(pos)`（:69-88，O(log n) 二分）；
- ASCII-only 文件（`pm.IsAsciiOnly()`，:64）零开销直通；
- `parsediag` 的 len 按 §2.3.5 规则两端点分别转换后作差；
- **自检**：`# text-utf16-len` 头 = `pm.UTF8ToUTF16(len(text))`；C# 侧 comparer 加载语料时断言 `content.Length == text-utf16-len`，防两侧解码分歧（BOM、无效序列）在 diff 中变成噪音。BOM 约定：两侧都**不剥 BOM**，按首字符 U+FEFF 参与偏移（scanner 自己处理 BOM 跳过——它属于 trivia）。

C# 消费端**不做任何转换**：夹具值直接与 `Scanner.TokenStart` 等比较。这就是主计划「位置已经 PositionMap 预转 UTF-16，C# CI 无需 Go 工具链」的全部含义。

### 2.5 夹具目录布局与分片

原始产物布局（dumptool `-out` 下）：

```
<out>/
  tokens/compiler/a.ts.tokens.txt
  rescan/compiler/a.ts.rescan.txt
  ast/conformance/es6/x.ts.ast.txt
  parsediag/..., symbols/..., flow/...
```

约 1.9 万语料 × 6 族 ≈ 11 万小文件，不宜直接入 git。入库形态为**按族 × 顶层目录分片的 zstd tar 包**：

```
fixtures/                        # 仓库根（裁决 R14：自有夹具移出 testdata/，testdata/ 保持 tsgo 纯镜像）
  fixtures.lock.json
  jsnum/                         # jsnum 族（§2.7.1，非分片打包）
  tokens.compiler.tar.zst        # ≈ 每片目标 ≤64MB（压缩后）
  tokens.conformance-00.tar.zst  # conformance 大目录按相对路径 xxh3_64 % 8 再切 8 片
  ...
  ast.compiler.tar.zst
  ...
```

- 打包由 `tools/dumptool/pack.ts`（或 shell）完成：tar 内路径 = 夹具相对路径，条目按路径码点序排序、mtime 归零 ⇒ **打包可复现**（同输入同字节，shard 哈希稳定）；
- 存储通道默认 **Git LFS**（估算压缩后总量数百 MB 级，普通 git 对象会拖垮 clone）；若组织不可用 LFS，备选「独立 fixtures 仓库 + commit 引用」，见 §11 待澄清第 1 条；
- C# 测试首次运行时把分片解包到本机缓存 `%LOCALAPPDATA%/tscs-fixtures/<lock-hash>/`（Linux/macOS 对应 XDG cache），解包前校验分片 xxh3，解包过程持文件锁防并行测试进程互踩（§3.1）。

### 2.6 再生成流程与 pin 记录

`tools/dumptool/regen.sh`（CI 亦可跑，需要 Go 工具链——这是**唯一**需要 Go 的工序，且只在升级 pin/改协议时发生）：

```
1. git clone <tsgo-url> --revision <pin>       # pin 读取自 fixtures.lock.json / 00-overview.md 钉扎记录
2. git -C tsgo submodule update --init _submodules/TypeScript
3. git -C tsgo am ../patches/*.patch           # P1 纪律：应当零冲突
4. go run ./cmd/dumptool -corpus ... -out /tmp/fixtures
5. tools/dumptool/pack.ts /tmp/fixtures fixtures/
6. 更新 fixtures/fixtures.lock.json；git add fixtures
```

`fixtures.lock.json`（схema normative）：

```json
{
  "protocolVersion": 1,
  "tsgoCommit": "<40-hex>",
  "typescriptSubmoduleCommit": "<40-hex>",
  "patchsetHash": "<patches/ 目录内容的 xxh3_128 小写 hex>",
  "corpus": ["_submodules/TypeScript/tests/cases/compiler", "_submodules/TypeScript/tests/cases/conformance", "testdata/tests/cases"],
  "families": ["tokens","rescan","ast","parsediag","symbols","flow"],
  "generatedAt": "2026-07-11T00:00:00Z",
  "shards": [ { "name": "tokens.compiler.tar.zst", "files": 12345, "bytes": 1234567, "xxh3": "<hex>" } ]
}
```

规则：`tsgoCommit` 必须等于 [00-overview.md] 登记的钉扎 commit——CI 有一个 5 秒的 `fixtures-lock-check` job 做此断言；`-verify` 模式用于「补丁没变、语料没变则夹具必须逐字节不变」的再生成幂等检查。

### 2.7 语料外增补族：jsnum 与 encode

六个语料驱动族之外，dumptool 补丁分支另挂两个专用族（建成后追加进 fixtures.lock.json 的 `families` 与 `shards`，受同一协议版本与 `-verify` 幂等检查管辖）。它们不属 §3.5「checker 细粒度差分不建族」非目标的范围。

#### 2.7.1 jsnum 族（阶段 1 门禁夹具；行格式与边界集与 [10-phase-0-scaffolding.md] §2.4 一致）

- **生成命令**：`go run ./cmd/dumptool jsnum -seed 20260711 -count 1000000 -out fixtures/jsnum/`——独立子命令，不走 `-corpus` 主循环；一次性生成，仅协议变更或 pin 升级时重跑；
- **固定随机种子**：`rand.NewPCG(seed, seed)`（Go rand/v2，默认 seed=20260711，写入文件头与 fixtures.lock.json）；随机 double 取两个 uint64 拼位模式、滤除 NaN/Inf 后得到 ⇒ 同种子重跑逐字节幂等；
- **行格式**（100 万 double，每行逗号分隔）：`bits(hex u64),String(),ToInt32,ToUint32,FromString(String()) 位相等标志`——十六进制位模式 + 四向期望（`jsnum.Number` 的 toString 文本 / ToInt32 / ToUint32 / FromString(String()) 往返位相等 0|1）；
- **边界值集合**：随机段之前前置固定边界段——±0、次正规（最小/最大 subnormal）、2⁵³±1、1e21/1e-6 两侧邻域、DBL_MAX/DBL_MIN；
- **消费端**：阶段 1 出口硬门禁「100 万 double 夹具四向逐位相等」（[10-phase-0-scaffolding.md] §2.6），`tests/Ts.Compiler.Tests` 读 `fixtures/jsnum/` 逐位断言。

#### 2.7.2 encode 族（阶段 9 api 编码器逐字节对照；[04-codegen.md] §8 委托本文的规格）

- **生成命令**：`-families` 增补值 `encode`（默认集仍为六族，阶段 9 建族后由 regen.sh 显式带上）；对每个语料文件 `parser.ParseSourceFile` 后调 `encoder.EncodeSourceFile(sourceFile)`（tsgo/internal/api/encoder/encoder.go:397），返回字节原样写 `<相对路径>.encode.bin`；
- **格式**：二进制原样落盘，无 §2.3.1(6) 文本文件头（encode 是唯一二进制族，版本经 fixtures.lock.json 管辖）；打包复用 §2.5 分片机制；
- **消费端**：C# 侧对 `Ts.LanguageService/Api` 生成编码器（[04-codegen.md] §8 的 `Encoder.g.cs`）输出做逐字节 `SequenceEqual`，失败按 offset 报告首个分歧字节的 hex 窗口——保证官方 TS 客户端可直连 C# 服务端的线格式承诺；归阶段 9 门禁。

---

## 3. 差分测试框架（C# 侧消费者）

### 3.1 xUnit fixture 设计

```csharp
// tests/Ts.Compiler.Tests/Differential/FixtureStore.cs
public sealed class FixtureStore : IAsyncLifetime   // xUnit v3 collection fixture
{
    public string Root { get; private set; }         // 解包缓存根
    public FixtureLock Lock { get; private set; }    // fixtures.lock.json 反序列化（STJ 源生成上下文）
    // InitializeAsync: 读 lock → 校验 tsgoCommit 与 00-overview 钉扎一致 → 按需解包（FileStream 排他锁串行化）
    public IEnumerable<FixtureEntry> Enumerate(string family, string shardName);
}

[CollectionDefinition("differential")]
public sealed class DifferentialCollection : ICollectionFixture<FixtureStore> { }
```

**理论粒度取「每分片一个 Theory case」而非「每语料文件一个」**：11 万 case 会压垮 xUnit 发现与报告管道。每族一个测试类（`TokenDiffTests`/`RescanDiffTests`/`AstDiffTests`/`ParseDiagDiffTests`/`SymbolDiffTests`/`FlowDiffTests`），`[MemberData]` 枚举该族分片名（约 10–20 个 case/族），case 内顺序处理分片中每个语料文件：

```csharp
[Theory][MemberData(nameof(Shards))]
public void TokensMatch(string shard)
{
    var failures = new List<FileDiff>();
    foreach (var entry in _store.Enumerate("tokens", shard))
    {
        if (Filter is not null && !entry.Path.Contains(Filter, StringComparison.Ordinal)) continue;
        var actual = TokenDumper.Dump(entry.SourceText, entry.IsTsx);   // 复刻 §2.3.2 协议
        if (actual != entry.FixtureText)
            failures.Add(new FileDiff(entry.Path, UnifiedDiff.Text("fixture", "actual", entry.FixtureText, actual)));
        if (failures.Count >= MaxReportedDiffs && !FullReport) break;
    }
    Assert.Empty(failures);   // 失败消息 = §3.3 的统一 diff 拼接
}
```

环境开关（本地调试用）：`TSCS_DIFF_FILTER=<子串>` 只跑匹配语料；`TSCS_DIFF_FULL=1` 不截断报告；`TSCS_DIFF_MAX=<n>` 每分片报告上限（默认 10 个文件、每文件 diff 截 200 行）。

### 3.2 语料枚举规则

- 语料集合 = fixtures.lock.json 的 `corpus` 三根目录递归 `*.ts`/`*.tsx`（与 dumptool 完全一致；C# 侧**不自行枚举文件系统**，而是枚举夹具条目——夹具即语料清单，天然防两侧枚举漂移）。语料原文随夹具打进分片（`src/` 前缀条目），保证「夹具 ↔ 输入」原子配对，C# 侧无需依赖 submodule 检出即可跑 B 类测试；
- **不做 `// @filename` 拆分**：多文件测试头（`test_case_parser.go` 语法）对 scanner/parser 而言只是注释；两侧都对原始全文操作，等价且免去把 makeUnitsFromTest 提前到阶段 2 的依赖。多文件语义留给 C 类 conformance（§4.3）；
- 金丝雀语料（深嵌套二元链/条件类型/超长模板，[01-architecture.md] §4.6）放 `testdata/tests/cases/compiler/` 下以 `canary` 前缀命名，随语料自然进入夹具与后续 conformance。

### 3.3 失败报告：统一 diff

所有差分失败一律渲染为 unified diff（3 行上下文，头 `--- fixture.<path>` / `+++ actual.<path>`），实现复用 §4.6 移植的 patience diff（`Ts.TestUtil.Diff.UnifiedDiff`）。同时把 `actual` 全文写到 `testdata/baselines/local/differential/<family>/<path>`，与 conformance 的 local 目录习惯一致，便于用外部 diff 工具复查。

### 3.4 并行度与内存控制

- xUnit 并行：`differential` collection 内 Theory case（分片）默认并行，`maxParallelThreads = Environment.ProcessorCount`（xunit.runner.json 提交入库）；分片内文件**串行**处理并即时丢弃（不缓存 AST），单分片峰值内存 ≈ 最大单文件 AST + 夹具文本；
- 分片压缩条目流式读取（`ZstdSharp` + `TarReader`，测试专用依赖，进 Directory.Packages.props）；解包缓存允许 `TSCS_FIXTURE_CACHE=off` 时全内存流式（CI 磁盘紧张时）；
- **大栈纪律**：`TokenDumper` 纯迭代可在普通线程；`AstDumper`/`SymbolDumper`/`FlowDumper` 调 parser/binder 递归，必须经 `LargeStackExecutor.Run(...)` 包裹（[01-architecture.md] §4.6 E1/E2 入口清单；Debug 构建有 `Debug.Assert(OnLargeStackThread)` 自动拦截违规）。

### 3.5 阶段 5 后的角色

conformance 接管验收货币后，B 类降级为**回归护栏**：每 PR 仍全量跑 tokens/ast/parsediag（快，纯 CPU），symbols/flow 降为 main 分支跑。「不扩充新族」限定为 **checker 细粒度差分不建族**（成本高且 conformance `.types/.symbols` 已是等价 oracle），此为明确的非目标；jsnum（阶段 1）与 encode（阶段 9）两族为既定规划，显式豁免（§2.7）。

---

## 4. conformance runner 移植

### 4.1 移植范围与文件映射

| Go 源（LOC 实测） | C# 目标 | 说明 |
|---|---|---|
| internal/testrunner/runner.go (14) | Ts.TestUtil/Harness/IRunner.cs | `EnumerateTestFiles`/`RunTests` 接口 |
| internal/testrunner/compiler_runner.go (583) | Ts.Conformance.Tests/CompilerBaselineRunner.cs | 本章主体 |
| internal/testrunner/test_case_parser.go (292) | Ts.TestUtil/TestCaseParser.cs | §4.3 |
| internal/testutil/baseline/baseline.go (250) + testmain.go (103) | Ts.TestUtil/Baseline/* | §4.6、§4.9 |
| internal/testutil/harnessutil/harnessutil.go (1,234) | Ts.TestUtil/Harness/Harness.cs 等 | CompileFiles/选项解析/Tracer |
| internal/testutil/harnessutil/sourcemap_recorder.go (365) | Ts.TestUtil/Harness/SourceMapRecorder.cs | §5.2 垫片所在地 |
| internal/testutil/tsbaseline/*.go (error 260/js_emit 285/type_symbol 490/sourcemap 124+34/module_resolution 18/util 71) | Ts.TestUtil/TsBaseline/* | 六个基线写出器 |
| gotest.tools/v3 assert | xUnit Assert + 少量适配 | `assert.Check`（不中断）→ 收集式断言 |
| github.com/peter-evans/patience | Ts.TestUtil/Diff/Patience.cs | §4.6，**必须逐算法移植** |

### 4.2 双模式（本地/子模块）与测试入口

`NewCompilerBaselineRunner(testType, isSubmodule)`（compiler_runner.go:59-72）的两个正交维度原样保留：

- **testType**：`conformance` / `compiler`（regression）两套子目录与 suite 名（:36-48）；
- **isSubmodule=false（本地模式）**：语料 `testdata/tests/cases/<suite>`（tsgo 本机实测 297 文件），基线子目录 `reference/<suite>/`；
- **isSubmodule=true（子模块模式）**：语料 `_submodules/TypeScript/tests/cases/<suite>`，基线子目录 `reference/submodule/<suite>/`，并额外产出 `.diff` 三根目录比对（§4.7）。

C# 入口与 Go 的 `TestLocal`/`TestSubmodule`（compiler_runner_test.go:14-18）对应：

```csharp
public class ConformanceTests
{
    [Theory][MemberData(nameof(LocalTestFiles))]      public void Local(string file)     => RunOne(file, isSubmodule: false);
    [Theory][MemberData(nameof(SubmoduleTestFiles))]  public void Submodule(string file) => RunOne(file, isSubmodule: true);
}
```

这里**恢复每文件一个 Theory case**（与 B 类不同）：conformance 用例天生独立、失败定位价值高，且 Go 侧同样是 per-file `t.Run`。约 1.9 万 case 的 xUnit 发现开销实测后若超预算，退回分片聚合模式（本文允许两种，跑批器抽象不变）。入口前置检查移植：重复文件名断言（compiler_runner_test.go:38-45）、submodule 未检出时跳过（`repo.SkipIfNoTypeScriptSubmodule` → `Assert.Skip`）、每次运行先清 `baselines/local/[diff/]<suite>`（compiler_runner.go:151-157）。

**确定性检验开关**（§1.2）：`TSCS_SINGLE_THREADED=1` 让 CompileFiles 构造 Program 时用串行 WorkGroup；CI 的确定性 job 以同一基线目录跑两遍取全等（基线 diff 本身就是全等判据，无需额外比较器）。

`SkipUnsupportedCompilerOptions`（harnessutil.go:1205-1234）原样移植：AMD/UMD/System、node10/classic 解析、`esModuleInterop=false`、`baseUrl`、`outFile`、ES5 target、`alwaysStrict=false` 等 tsgo 不支持的旧行为直接 Skip——**这不是我们的自由度**，C# 侧 Skip 集必须与 tsgo 完全一致，否则基线集合对不上。`skippedTests`（compiler_runner.go:86-135，45 个依赖 built/typescript.d.ts 或已删除选项的用例；上游 pin 升级时此计数以脚本对源码实数，不手抄）与 `skippedEmitTests`（:393-402，8 个并行 emit 非确定用例）两张表逐字拷贝，注释保留。

### 4.3 test_case_parser：多文件头指令集全解

`ParseTestFilesAndSymlinksWithOptions`（test_case_parser.go:131-272）是 conformance 与 fourslash 共用的多文件拆分器，逐行状态机，指令语法（正则 :41-44）：

| 指令 | 语义 | 源码点 |
|---|---|---|
| `// @filename: path` | 开启新文件单元；首个 filename 之前若有非注释内容且不允许隐式首文件则 panic | :192-236, :213-214 |
| `// @<option>: value` | 全局选项（编译/harness 选项），键小写化；重复且值不同时 Go 注释掉了 panic（`!!! This would break existing submodule tests`，:183-186）——C# 同样容忍 | :163-189 |
| `// @symlink: a,b`（文件内） | 为当前文件登记 symlink 目标（逗号分列） | :171-177 |
| `// @link: src -> dst`（独立行） | 全局 symlink 映射（linkRegex，注意方向：`symlinks[dst]=src`） | :284-292 |
| `// @currentDirectory: d` | 记录当前目录（同时也进全局选项） | :167-169 |
| `emitthisfile` | 唯一的文件级 fourslash 指令（fourslashDirectives，:47） | :178-181 |

移植要点（逐条为 diff 承重项）：
1. 行拆分用 `\r?\n`（:18）；内容行拼接对 compiler 模式用 `Len()!=0` 判据丢弃首部空行、fourslash 模式（AllowImplicitFirstFile）用 `seenContentLine` 保留（:243-253 注释写明两种语义的出处）；
2. 单文件无指令时文件名取 `GetBaseFileName(fileName)`（:258-260）；EOF 后必推最后一个单元（:263-269）；
3. `makeUnitsFromTest`（:51-111）在拆分后检测 tsconfig 单元（`GetConfigNameFromFileName`：仅 `tsconfig.json`/`jsconfig.json` 基名，harnessutil.go:1197-1203），命中则用 **JSON ScriptKind 的真 parser** 解析并走 `ParseJsonSourceFileConfigFileContent`，并把该单元从列表移除——这要求阶段 4 的 tsoptions 移植完成，故 conformance 跑批器完整可用性挂在阶段 4 出口（与主计划一致：阶段 3 只移植 testrunner 骨架 + AST 差分）；
4. 编译单元分组启发式（compiler_runner.go:316-336）：无 tsconfig 时看**最后一个**单元是否含 `require(` 或 `reference path` 或设置了 `@noImplicitReferences`——是则最后单元为编译入口、其余为 otherFiles，否则全部同编译会话。`baseurl` 的相对路径归一化（:317-319）一并移植。

`extractCompilerSettings`（test_case_parser.go:274-282）在拆分前先全量抓取 `@option`（值去尾 `;`），供 vary-by（§4.4）。

### 4.4 vary-by 配置矩阵

`getCompilerVaryByMap`（compiler_runner.go:163-188）：从 `OptionsDeclarations` 过滤出「非命令行专用 && (boolean|enum) && 影响程序结构/emit/解析/诊断/…」的选项名 + 显式补充 `noEmit`、`isolatedModules`，全小写成集合。C# 侧由 tsoptions 移植（阶段 4）提供同一张声明表，**过滤谓词逐字段直译**——vary-by 集合错一个，测试名与基线文件名整体漂移。

`GetFileBasedTestConfigurations`（harnessutil.go:1007-1043）：值列表 `splitOptionValues`（:1054-；支持 `*` 全展开、`-`/`!` 排除）、笛卡尔积上限 25（:1016-1018 超限 Fatal）、配置描述 = 按键排序的 `k=v(小写)` 逗号串（:995-1005）。测试显示名 `basename + " " + config.Name`（compiler_runner.go:196-199）；基线文件名 `configuredName = 去扩展名(描述)扩展名`（:271-277，如 `file(strict=true).ts`）。**键排序**：Go `slices.Sorted(maps.Keys)` 是字节序 → C# 用 §2.3.6 的码点序 comparer（选项名全 ASCII，实际等价 ordinal，但统一走同一 comparer 防未来非 ASCII 键）。

### 4.5 八段验证流水

`runSingleConfigTest`（compiler_runner.go:206-223）顺序固定，C# 保持：

| # | verify | 产物 | 移植要点 |
|---|---|---|---|
| 1 | verifyDiagnostics（:363-391） | `<name>.errors.txt` | DoErrorBaseline；无错误时 `baseline.NoContent`；submodule 模式带 DiffFixupOld（剥 `==== ./` 前缀，:374-388）；§5.1 垫片在此 |
| 2 | verifyJavaScriptOutput（:404-433） | `<name>.js` | 头部 = 相对 TestDataPath 的路径，submodule 剥 4 段前缀（:415-419）；js_emit_baseline.go 的 `//// [header] ////` 拼装、JSON 输出的错误内联（js_emit_baseline.go:57-66）、d.ts 二次编译错误段 `//// [DtsFileErrors]`（:87-97）、noCheck 双跑比对（:99-121） |
| 3 | verifySourceMapOutput（:435-453） | `<name>.js.map` | sourcemap_baseline.go；inlineSourceMap 校验（:28-33）、数量断言（:41-43）、sokra 预览链接 base64 附录（:104-115） |
| 4 | verifySourceMapRecord（:455-473） | `<name>.sourcemap.txt` | sourcemap_recorder.go 全文直译；§5.2 垫片在此 |
| 5 | verifyTypesAndSymbols（:475-504） | `<name>.types` / `<name>.symbols` | type_symbol_baseline.go（490 LOC）；`NoTypesAndSymbols` harness 选项短路（:476-479） |
| 6 | verifyModuleResolution（:506-519） | `<name>.trace.json` | `traceResolution` 开启才跑；`SkipDiffWithOld=true`（不产 .diff）；TracerForBaselining 清洗（§6.4） |
| 7 | verifyUnionOrdering（:528-551） | 无基线，纯断言 | union 类型反转/洗牌 10 次后 `CompareTypes` 排序回原序；洗牌用 `rand.NewPCG(1234,5678)` —— C# 侧**移植 Go rand/v2 的 PCG 实现**（算法公开、几十行）保证同序列，否则断言强度不等价 |
| 8 | verifyParentPointers（:553-583) | 无基线，纯断言 | 全树 parent 一致性；跳过默认库文件 |

格式共性（tsbaseline 写出器逐处核对）：harness 换行 = `"\r\n"`（error_baseline.go:24）；编译默认 NewLine=CRLF、SkipDefaultLibCheck=true、NoErrorTruncation=true（harnessutil.go:96-102）；路径前缀清洗表（util.go:24-42：`/.ts/`→``、`/.lib/`→``、`/.src/`→``、`bundled:///libs/`→`` 等）；lib 位置抹除 `(--,--)` 与 `:--:--`（error_baseline.go:31-32,115,140）；`sanitizeTestFilePath` 的非法字符与 `../`→`__dotdot/` 处理（util.go:65-71）。这些常量与替换器全部进 `Ts.TestUtil/TsBaseline/Util.cs`，一字不差。

### 4.6 baseline.Run 的 diff 机制与本地产物

`baseline.Run`（baseline.go:30-98）与 `writeComparison`（:195-244）移植为 `BaselineRunner.Run(testContext, fileName, actual, opts)`：

- 路径：`localRoot=testdata/baselines/local`、`referenceRoot=testdata/baselines/reference`、`submoduleReferenceRoot=_submodules/TypeScript/tests/baselines/reference`（:246-250）；
- `NoContent = "<no content>"` 哨兵（:28）；actual 为空串直接 panic（:196-198）——写出器有义务显式给 NoContent；
- 不匹配时：写 local 文件（应删除的基线写 `<name>.delete` 空文件，:220-230）+ 报错文案区分「新基线」「基线已变」「submodule 缺失/不匹配」（:232-242）；
- **.diff 生成**（submodule 模式，:48-98）：与 Strada 原基线（submoduleReferenceRoot）比对产生 patience unified diff，头 `old.<file>`/`new.<file>`、3 行上下文，hunk 头重写为 `@@= skipped -X, +Y lines =@@`（:162-181，防行号连锁抖动）。**因为 `reference/submodule*/**.diff` 是从 tsgo 同步的既有基线，我们的 diff 文本必须与 Go 的 `github.com/peter-evans/patience` 输出逐字节一致 ⇒ 该库必须按算法移植成 `Ts.TestUtil.Diff.Patience`**（patience diff + `UnifiedDiffTextWithOptions` 版式），并把 tsgo 的 baseline_test.go 用例一并移植作其单测。不得用 DiffPlex 等现成库替代（版式不保证一致）。

### 4.7 submodule 三根目录与 submoduleAccepted/Triaged 语义

背景（[00-overview.md]:293 只读纪律的补全）：tsgo 用 `.diff` 基线管理「与 Strada 的已知差异」。`baseline.Run` 对每个 `.diff` 同时维护三个根（baseline.go:56-97）：

- `submodule/<suite>/<file>.diff`：默认根，存放未定性的差异；
- `submoduleAccepted/<suite>/<file>.diff`：`testdata/submoduleAccepted.txt` 清单（tsgo 实测 1,426 行，`#` 注释 + 空行跳过，:100-124）命中的差异——上游认定「新行为正确，接受偏离 Strada」；
- `submoduleTriaged/<suite>/<file>.diff`：`submoduleTriaged.txt`（87 行）命中——已分诊待修；
- 清单键 = `"<suite>/<file>.diff"`（:62-63）；同键出现在两个清单 → `t.Fatalf`（:67-69）；diff 结果只写命中的那个根，另两个根写 NoContent 占位（:85-97）。

**C# 侧使用方式**：这两份 txt 与三根目录的全部 reference 内容都随基线同步自 tsgo，我方跑批器在 submodule 模式**原样复刻**该路由逻辑——目的不是维护差异（那是上游的事），而是让我们的 `.diff` 产物落到与 reference 相同的位置从而参与逐字节比对。换言之：**通过 = 我们与 tsgo 的差异集合（相对 Strada）完全一致**。清单文件解析器（跳过空行/`#`）与「双清单冲突 Fatal」一并移植。

### 4.8 accept 工作流决定：`dotnet run --project tools/baseline-accept`

Go 侧 `hereby baseline-accept`（tsgo/Herebyfile.mjs:964-996）语义：把 `baselines/local/**`（除 `*.delete`）整树拷入 `baselines/reference/`；对每个 `*.delete` 删除 reference 对应文件并删除 .delete 本身。

**决定：实现 `tools/baseline-accept`（约 80 行 C# 控制台程序），用 `dotnet run --project tools/baseline-accept` 调用；不采用 `UPDATE_BASELINE=1` 环境变量直写方案。** 理由：
1. 与 tsgo 工作流同构（先跑测试看 local diff，人审后一次 accept），审阅点天然存在；环境变量直写把「产生」与「接受」合并，绕过人审；
2. 环境变量方案与 xUnit 并行写 reference 有竞态，且违背 [00-overview.md]:293 的 reference 只读纪律（该纪律要求测试进程对 reference 目录零写入）；
3. .delete 语义（基线应删除）在直写方案里无自然表达。

**使用政策**（与只读纪律的调和）：conformance 的 reference 是 tsgo 同步物，日常**禁止** accept——diff 失败只能改 C# 代码收敛。accept 工具的合法场景仅两类：(a) 升级上游 pin 后按 [00-overview.md]:241 流程全量重同步的核对性运行；(b) 我方自有的非同步基线（本地新增金丝雀/回归用例在本地模式下的基线、fourslash 状态基线的 C# 特有部分）。CI 强制：PR 中对 `testdata/baselines/reference/**` 的改动必须携带 `baseline-sync` 标签（升级 pin 的 PR）否则拒绝。

### 4.9 未使用基线追踪

Go 机制：`baseline.Track()`（testmain.go:35-62）在 TestMain 注册、经 `TSGO_BASELINE_TRACKING_DIR` 输出「本次运行写过的基线相对路径」清单，Herebyfile 聚合后对 reference 里从未被引用的文件报错并生成 .delete（Herebyfile.mjs:749-757）。C# 等价：`Ts.TestUtil.Baseline.BaselineTracker`（进程级 `ConcurrentBag`，xUnit v3 assembly fixture 在测试装配卸载时按 `TSCS_BASELINE_TRACKING_DIR` 写出）；CI 的 `unused-baselines` job 在全量套件（conformance+fourslash+tsc）后聚合比对 reference 全集。阶段 5 达 90% 前该 job 只告警不失败（大量基线对应尚未启用的用例）。

### 4.10 分级期间的 verifier 子集开关

阶段 5 的分级门禁（errors 25%→…→100%，再 types/symbols）意味着跑批器要能只开启部分 verifier 并统计通过率。实现：`TSCS_CONFORMANCE_VERIFIERS=errors[,output,sourcemap,record,types,resolution,union,parents]` 环境变量 + 通过率汇总写 `baselines/local/summary.json`（按 suite × verifier 计数），[11-progress.md] 每次门禁评审时登记。默认（不设变量）= 全开，等于最终形态。

---

## 5. 两个 UTF 垫片（基架兼容层）

背景：主计划 §一.6 已论证「基线中一切行列位置都是 UTF-16」，删除 PositionMap 对基线安全；但测试基架里有两处 Go 按字节/rune 计数的行为**直接印进基线字节**，C#（UTF-16 原生）必须刻意垫平。两垫片都只存在于 `Ts.TestUtil`，生产代码零污染。

### 5.1 垫片 1：errors.txt 波浪线行按 Unicode 码点计数

Go 现场（tsgo/internal/testutil/tsbaseline/error_baseline.go:196-221）：

- 波浪线前缀：`nonWhitespace.ReplaceAllString(line[:squiggleStart], " ")`（:211）——Go regexp `\S` 按 **rune** 匹配，每个非空白**码点**替换为恰一个空格；空白字符（Go `\s` = `[\t\n\f\r ]`，注意**不含 \v**）原样保留（tab 仍是 tab，对齐承重）；
- 波浪线本体：`strings.Repeat("~", utf8.RuneCountInString(line[squiggleStart:squiggleEnd]))`（:212-214）——按**码点数**画波浪线（星面字符 = 1 个 `~`）。

C# 若朴素地按 `char`（UTF-16 unit）计数，星面字符会产出 2 空格/2 波浪线 → 基线 diff。实现（`Ts.TestUtil/TsBaseline/ErrorBaseline.cs`）：

```csharp
// squiggleStart/squiggleEnd 在 C# 侧是 UTF-16 偏移（诊断 pos 与 lineStarts 已是 UTF-16——
// 与 Go 的字节偏移不同空间，但下面的码点级折叠使最终文本两侧一致）
static string PadLikeGoNonWhitespace(ReadOnlySpan<char> prefix)
{
    var sb = new StringBuilder(prefix.Length);
    for (int i = 0; i < prefix.Length; i++)
    {
        char c = prefix[i];
        if (c is '\t' or '\n' or '\f' or '\r' or ' ') { sb.Append(c); continue; }   // Go \s 集合
        if (char.IsHighSurrogate(c) && i + 1 < prefix.Length && char.IsLowSurrogate(prefix[i + 1]))
            i++;                                   // 代理对 = 1 码点 = 1 空格
        sb.Append(' ');
    }
    return sb.ToString();
}

static int CodePointCount(ReadOnlySpan<char> s)
{
    int n = 0;
    for (int i = 0; i < s.Length; i++, n++)
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
    return n;
}
// 波浪线：new string('~', CodePointCount(line.AsSpan(squiggleStart, squiggleEnd - squiggleStart)))
```

边界备注：(a) 孤立代理项按 1 计（Go 对无效 UTF-8 每字节计 1 个 U+FFFD rune，语料为有效文本时两者不相遇；防御性对齐即可）；(b) `squiggleStart` 由诊断 pos 与行首差值算出，token 边界对齐，正常不会劈开代理对——若劈开（恶意语料），高代理单独计 1，与 Go 劈 UTF-8 序列的字节行为不逐一对应，登记 [12-risks.md] 为理论残留。其余逻辑（`squiggleEnd = max(squiggleStart, min(squiggleStart+length, len(line)))` :213、跨行错误续画、末行末字符错误的 fake nextLineStart :187-192、`markedErrorCount` 与总数断言 :225,251）逐行直译，仅计数函数换用上面两个垫片。

### 5.2 垫片 2：.sourcemap.txt 续行标记终列 = JS 行的 UTF-8 字节长 − 1

Go 现场（tsgo/internal/testutil/harnessutil/sourcemap_recorder.go:350-353）：

```go
jsFileText := w.getTextOfLine(currentJsLine+1, w.jsLineMap, w.jsFile.Content) // 保留了 Strada 的 +1 bug（注释原文）
if sw.prevEmittedCol < len(jsFileText)-1 {
    sw.writeSourceMapMarkerEx(nil, len(w.spansOnSingleLine), len(jsFileText)-1, true /*endContinues*/)
}
```

`jsFileText` 是 Go string ⇒ `len()` 是 **UTF-8 字节长**（且该行文本含行尾换行符；`getTextOfLine` :211-225 取 [lineStart, nextLineStart)）。续行标记 `N >^^^…->` 的 `^` 个数 = `endColumn - prevEmittedCol`，而 `prevEmittedCol` 来自 sourcemap 的 `GeneratedCharacter`（UTF-16 列）——**字节长与 UTF-16 列混用是 Strada 兼容 bug，必须原样复刻**。C# 实现：

```csharp
string jsFileText = GetTextOfLine(currentJsLine + 1, _jsLineMap, _jsFile.Content); // +1 bug 原样保留
int endColumn = Encoding.UTF8.GetByteCount(jsFileText) - 1;                        // ← 垫片：UTF-8 字节长
if (_prevEmittedCol < endColumn)
    WriteSourceMapMarkerEx(null, _spansOnSingleLine.Count, endColumn, endContinues: true);
```

同文件另一处是**反向简化**而非垫片：`writeSourceMapSourceText`（:294-302）在 Go 里要用 `scanner.ComputePositionOfLineAndUTF16Character`（tsgo/internal/scanner/scanner.go:2742）把 sourcemap 的 UTF-16 列转回字节位再切 `tsCode`；C# 里位置本来就是 UTF-16，直接 `tsCode[prevWrittenSourcePos..sourcePos]` 切子串即可，该转换函数在测试基架中不再需要。其余（markerId 版式 :244-259、`writeSourceMapIndent` 按 prevEmittedCol 空格缩进 :269-274、span 详情 `Emitted(l,c) Source(l,c) + SourceIndex(n) [name (x)|nameIndex (n)]` :123-137、解码错误注入 :139-167、`recordNewSourceFileSpan` 的 `char==line` Strada bug :169-176）逐行直译。

### 5.3 判别性测试文件清单

以下文件构成两垫片的**最小判别集**，在 CI 中单列为 named smoke tests（阶段 5/6 分级早期就必须绿，防垫片回归被海量 diff 淹没）。清单由本机对 `testdata/baselines/reference` 全量扫描得出：

**垫片 1（errors.txt 含星面字符，扫描谓词：文件含 U+10000..U+10FFFF）——恰 3 个：**
- `submodule/compiler/regularExpressionWithNonBMPFlags.errors.txt`（主计划引用的判别证据：`(7,23)` 为 UTF-16 列；其波浪线行含 𝘴𝘪𝘮 等数学字母星面字符）
- `submodule/compiler/regularExpressionCharacterClassRangeOrder.errors.txt`
- `submodule/compiler/unicodeEscapesInNames02(target=es2015).errors.txt`

**垫片 2（.sourcemap.txt 含非 ASCII，扫描谓词：任意非 ASCII 字节）——恰 3 个：**
- `submodule/compiler/sourceMap-LineBreaks(target=es2015).sourcemap.txt`
- `submodule/compiler/sourceMapPercentEncoded.sourcemap.txt`（首部即 CJK 文件名 + percent-encoded mapUrl，兼验 URL 编码路径）
- `submodule/compiler/unicodeEscapesInNames02(target=es2015).sourcemap.txt`

另备一组 BMP 非 ASCII（CJK）errors 用例作 UTF-16 主决策的冒烟（任选 `chineseCharacterDisplay` 类基线），它们不判别垫片 1（BMP 码点 = 1 UTF-16 unit）但判别「字节 vs UTF-16」底座。

### 5.4 休眠路径备案

`type_symbol_baseline.go:234-241`：`.types` 基线的 underline 行按 `len(lineText)`（字节）补空格——但 `result.underline` 在当前 tsgo 恒为空串，路径休眠。C# 直译时按 UTF-16 `lineText.Length` 写即可，并在代码处留 `// PORT-NOTE: dormant; byte-pad in Go (type_symbol_baseline.go:236)` 注释，[12-risks.md] 登记：若上游激活该路径需补第三个垫片。

---

## 6. tsc/tsbuild 基线（阶段 7）

### 6.1 testFs 的 .tsbuildinfo 拦截与 FakeTSVersion 清洗

tsgo/internal/execute/tsctests/fs.go:17-84 的 `testFs`（vfs 包装器）是 tsc/tsbuild 基线可复现的关键：

- **WriteFile 拦截**（:53-84）：凡 `.tsbuildinfo` 写出，反序列化后把 `Version == core.Version()` 替换为 `"FakeTSVersion"`（常量定义 harnessutil.go:41）再序列化落盘；同时额外写一份 `<path>.readable.baseline.txt`（§6.2）。反序列化失败 = panic（:80-82），倒逼 buildinfo JSON 形态正确；
- **ReadFile 拦截**（:35-51）：读到 FakeTSVersion 换回真实版本，使增量编译逻辑在测试里正常自校验；
- 清洗时机在 **json 往返**上：这直接依赖 [01-architecture.md] 的 .tsbuildinfo 字节保真实现（UnsafeRelaxedJsonEscaping、声明序、omitzero、7 种多态形状）——testFs 的 Marshal 出来的字节必须与生产写出器一致，否则「增量=全量输出」门禁自我矛盾。C# 落点 `Ts.TestUtil/Harness/TestFs.cs`，复用生产 `Ts.Compiler` 的 BuildInfo 序列化器。

版本清洗的另外两处：`tsctests/sys.go:452-459` 对 CLI 输出消息做 `'<realVersion>'`→`'FakeTSVersion'` 全文替换（含本地化变体）；`tscbuild_test.go:3851-3870` 有用例手工替换 buildinfo 版本制造「旧版本」场景——移植这些用例时保留其字符串手术。

### 6.2 readable buildinfo 写出器

`toReadableBuildInfo`（tsgo/internal/execute/tsctests/readablebuildinfo.go:209-238）把紧凑 buildinfo 展开为人类可读基线：fileId→路径、fileIdList→路径数组、诊断/emit 信号量展开（:240-264 起），最后 `json.MarshalIndent(&readable, "", "  ")`——**2 空格缩进**。C# 侧按 [07-aot-constraints.md] 政策 9 用**手写 writer**（非 STJ 反射亦非源生成）实现 `ReadableBuildInfoWriter`，理由同 .tsbuildinfo：逐字段 omitzero、键序 = struct 声明序、非 ASCII 直通，这些旋钮 STJ 缩进模式给不全。427 行 readablebuildinfo.go 全文直译（含 `readableBuildInfoResolvedRoot` 的 `[resolved, root]` 二元组形状，:197-207）。

### 6.3 size 字段

`Size: len(buildInfoText)`（readablebuildinfo.go:221，json tag `size,omitzero` :38）——是**紧凑 buildinfo 文本的字节长**，且该文本是「版本已换成 FakeTSVersion、经 `fsbaselineutil.SanitizeInternalSymbolName` 清洗后」的字符串（fs.go:73-76）。C#：`Encoding.UTF8.GetByteCount(sanitizedCompactJson)`（Go `len(string)` = UTF-8 字节数；buildinfo 含文件路径可有非 ASCII，用 `.Length` 会错）。主计划 §一.5「size=紧凑文本字节长」即此。

### 6.4 模块解析 trace 清洗器

`TracerForBaselining`（harnessutil.go:505-...）：版本替换（:529）+「cached lookups」措辞折平为非缓存版本（:533-556，带 per-path 首见去重的 packageJsonCache）——`.trace.json` 基线与 tsc 基线共用。直译，放 `Ts.TestUtil/Harness/BaselineTracer.cs`。

阶段 7 其余（watch 场景脚本、`sys` 伪时钟、增量场景 DSL——tscbuild_test.go 4k 行表驱动用例）按 A 类移植单测处理，归 [06-module-inventory.md] 的 execute/tsctests 条目；本文只锚定上面四个字节承重机制。

---

## 7. fourslash 策略（阶段 8）

### 7.1 tsgo 机制考古

- **运行时**（tsgo/internal/fourslash/，共 9,770 LOC）：`NewFourslash`（fourslash.go:133-150）把测试内容经 `ParseTestData`（test_parser.go，标记语法：`/**/` 匿名标记、`/*name*/` 命名标记、`[| |]` 范围、`{| "k":"v" |}` 数据标记）装进 vfs，启动**进程内 LSP 会话**（lsptestutil.LSPClient），断言走 LSP 协议层——所以 fourslash 同时是 LSP 集成测试；
- **生成器**（_scripts/convertFourslash.mts，4,715 行）：用 TS 编译器 API 解析 Strada 仓库 `tests/cases/fourslash/**`（含 server/ 子目录，:11, :127），把每个 `verify.*/goTo.*/edit.*` 语句翻译为 Go 调用，输出 `tests/gen/<name>_test.go`（本机实测 3,796 个）；无法翻译的语句抛错记入 `unparsedTests.txt`（2,570 行，格式 `<file> parse error: "<原因>"`）；`manualTests.txt`（177 行）列出**不生成**、由人手写维护于 `tests/manual/` 的用例（`makeManual.mts` 负责把 gen 迁为 manual）；codeFix 白名单机制（allowedCodeFixIds/DescriptionPrefixes，:33-57）控制只转换 Go 已实现的修复；
- **失败管理**：生成的每个测试首行 `fourslash.SkipIfFailing(t)`（skip_if_failing.go:44-53）查 `failingTests.txt`（459 行，一行一个 `TestXxx`），命中即 Skip；`TSGO_FOURSLASH_IGNORE_FAILING=1` 强制全跑；`updateFailing.mts` 用 `go test -json` 全跑后重写 failingTests.txt 与 `crashingTests.txt`（updateFailing.mts:29-33）；
- **基线**：`baselineutil.go`/`statebaseline.go` 把 LSP 响应渲染为 `testdata/baselines/reference/fourslash/**` 基线，复用 §4.6 的 baseline.Run（含 submodule .diff 机制，故 fourslash 也出现在 submoduleAccepted.txt 中）。

### 7.2 C# 侧改造：convertFourslash 发射 xUnit

**决定：fork 而非重写。** 拷贝 convertFourslash.mts 至 `tools/generator/convert-fourslash-csharp.mts`，**解析半边（parseFourslashStatement 及其 4,000 行命令识别逻辑）原样保留，只替换发射半边**（generateGoTest → generateCSharpTest）。与 [04-codegen.md] 生成器族同库管理、随上游 pin 同步 rebase。

发射目标形态（与 Go gen 逐结构同构，保 grep 对照性）：

```csharp
// <auto-generated>convert-fourslash-csharp; DO NOT EDIT.
// To modify this test, run "npm run makemanual autoImportProvider_importsMap2"</auto-generated>
namespace Ts.Fourslash.Tests.Gen;

public class AutoImportProvider_importsMap2Tests
{
    [FourslashFact]
    public void TestAutoImportProvider_importsMap2()
    {
        const string content = """
            // @Filename: /home/src/workspaces/project/tsconfig.json
            ...
            """;
        using var f = FourslashTest.New(TestContext.Current, capabilities: null, content);
        f.MarkTestAsStradaServer();
        f.VerifyImportFixModuleSpecifiers("", new[] { "#internal/foo.js" }, preferences: null);
    }
}
```

要点：
- 原始内容用 C# 11 raw string literal 发射（免去 Go 反引号拼接的 `` ` `` 转义特判，convertFourslash.mts:200-219 的 getTestInput 相应简化）；发射器需处理 raw literal 的边界情形（内容行含 `"""` 时升引号栏）；
- **测试名保持 Go 拼法 `TestXxx`**：failingTests/manualTests/unparsedTests 三清单可与 tsgo 同名 diff，上游同步时直接三方比对；
- `[FourslashFact]` = 自定义 FactAttribute；跳过走 xUnit v3 动态 skip：属性内或 `FourslashTest.New` 首行执行 `SkipIfFailing(TestContext)`——读嵌入的 `failingTests.txt`，命中且未设 `TSCS_FOURSLASH_IGNORE_FAILING` 时 `Assert.Skip("Test is in failingTests.txt")`（对位 skip_if_failing.go 语义）；
- 生成物入库为 `tests/Ts.Fourslash.Tests/Gen/*.g.cs`（与 AST 生成物同哲学：产物提交、diff 可审，[04-codegen.md]）；`dprint fmt` 一步换成 `dotnet format` 或直接由发射器产出规范格式。

### 7.3 运行时移植

`FourslashTest`（fourslash.go 5,815 LOC）+ test_parser.go(509) + baselineutil.go(1,182) + statebaseline.go(511) + semantictokens.go(130) → `tests/Ts.Fourslash.Tests` 的 Runtime/ 目录（或并入 Ts.TestUtil，定夺归 [06-module-inventory.md]）。依赖面即阶段 8 的 ls/lsp/project 移植；`ParseTestData` 复用 §4.3 的 TestCaseParser（`AllowImplicitFirstFile=true` 分支，test_case_parser.go:113-118, :147-151）。进程内 LSP 客户端复用 `Ts.LanguageService.Tests` 的 LSP 回放基础设施（stdio 帧编解码同一实现）。

### 7.4 四清单机制复刻

| 清单 | tsgo 语义 | C# 侧 |
|---|---|---|
| failingTests.txt | 已生成但当前失败 → 运行时 Skip | 同名文件放 `tests/Ts.Fourslash.Tests/Scripts/`，嵌入资源；`tools/fourslash-update-failing`（TS 脚本）驱动测试可执行文件（xUnit v3 项目本身是 exe，跑 `--report-xunit` 输出）在 `TSCS_FOURSLASH_IGNORE_FAILING=1` 下全跑并重写清单（对位 updateFailing.mts） |
| crashingTests.txt | 全跑时进程级崩溃的用例（updateFailing 产出；崩溃会杀掉 test 进程故须单列） | 同机制：update 脚本检测非零退出/未完成用例，二分定位后写清单；这些用例连 Skip 都不安全，由发射器直接**不发射**（`#if false` 包裹），清单变更需重新生成 |
| manualTests.txt | 不自动生成、人工维护 | 同语义；`npm run makemanual <name>`（makeManual.mts 移植）把 gen 文件迁到 Manual/ 并加入清单 |
| unparsedTests.txt | 转换器无法翻译的用例 + 原因 | 发射器自动重写（convertFourslash.mts:101-103 的排序输出保留），是移植进度的负空间清单 |

### 7.5 分批启用策略

阶段 8 门禁分级（主计划 §二：50%→90%→99%+）的操作化：

1. **一次性全量转换**：阶段 8 开工即运行转换器发射全部可解析用例（≈3,800 个起步，随 C# 侧能力放宽 codeFix 白名单递增）；
2. **failingTests.txt 初始 = 全量**：由 update 脚本首跑自动生成（预期几乎全失败），随 ls 功能落地持续收缩——**清单收缩曲线就是进度曲线**，每次收缩由 update 脚本产生、PR 审阅，[11-progress.md] 登记百分比；
3. **按功能族分批攻坚**，顺序对齐 ls 包移植顺序（[09-phases.md] 阶段 8 子门禁）：diagnostics → hover/quickinfo → definition/references → completions（最大族）→ rename/signatureHelp → codeFix/organizeImports → server 特有；每族攻坚以「该族用例在 failingTests 中清零」收口；
4. **分母定义**：50/90/99% 的分母 = 已转换/手写用例数，不含 unparsed/crashing（与 tsgo 的口径一致——它的通过率也只对生成集有意义）。已由 [09-phases.md] 裁决（R10）：分母取三段口径 289（顶层手写）+ 210（tests/manual/）+ 3,796（gen/）= 4,295，见 §11 待澄清第 4 条（已关闭）；
5. codeFix 白名单（convertFourslash.mts:33-57）起步拷贝 tsgo 当前集合，C# 实现新 codeFix 后同步放宽并重跑转换器。

### 7.6 fourslash 基线

`testdata/baselines/reference/fourslash/**`（含 submodule .diff 三根）已随基线整树同步；fourslash 的 baseline 断言走与 §4.6 同一 BaselineRunner，无新机制。注意 fourslash 基线大量属于 submoduleAccepted 清单（如 inlayHints 条目，submoduleAccepted.txt 实测含 `fourslash/inlayHints/...` 段），三根路由必须已就位（§4.7）。

---

## 8. AOT 双轨 CI 矩阵

### 8.1 双轨原则

主计划 §四.2：**日常测试跑 JIT（快、可调试），阶段门禁与主干额外对 AOT 发布产物复跑一致性套件**。AOT 特有分歧源（JIT 下不可见）：ILC 裁剪、静态构造顺序、RVA 静态数据、`InvariantGlobalization` 下的 API 行为、栈尺寸（发布产物线程属性）、以及任何漏网反射——所以 AOT 轨跑的必须是**同一套 conformance 判定**而非冒烟。

### 8.2 job 矩阵

| Job | 内容 | 平台 | 触发 |
|---|---|---|---|
| `lint-analyzers` | build + AOT/Trim analyzer 全开零警告（[07-aot-constraints.md] 门禁）+ fixtures-lock-check + 生成物 drift 检查（重跑生成器 diff 为空，[04-codegen.md]） | linux-x64 | 每 PR |
| `test-jit` | A 类单测 + B 类差分（tokens/rescan/ast/parsediag 全量；symbols/flow 见触发列）+ C 类 conformance（当前阶段已达标 verifier 子集）| linux-x64、windows-x64、macos-arm64 | 每 PR（win/mac 可降为 main+nightly 以省时，阶段门禁时三平台强制） |
| `aot-publish-smoke` | `dotnet publish /p:PublishAot=true` Ts.Cli 三平台 + hello 编译 + 金丝雀深嵌套语料（JIT/AOT 双跑，栈金丝雀见 [01-architecture.md] §4.6） | 3 平台 | 每 PR：linux；main/nightly：3 平台 |
| `aot-conformance` | 发布 `Ts.Conformance.AotHost`（§8.4）跑全量 C 类 + B 类夹具 diff；确定性双跑（并行 vs `--singleThreaded`） | 3 平台 | main 合入 + nightly + 每次阶段门禁签核 |
| `fourslash-jit` | D 类全量（Skip 机制生效态） | 3 平台 | 触及 ls/lsp/project/format 路径的 PR + main |
| `aot-e2e-compare` | §8.5：发布版 Ts.Cli vs tsgo 真实项目逐字节对比（含 .tsbuildinfo、增量=全量） | 3 平台 | nightly + 阶段 7/10 门禁 |
| `unused-baselines` | §4.9 聚合 | linux-x64 | nightly（阶段 5 ≥90% 后升为 main 必过） |
| `bench-trend` | §9 | 固定专用 runner | nightly 或 weekly |

阶段 10 出口（主计划：全一致性 + fourslash 在三平台 AOT 二进制上全绿）= `aot-conformance` + fourslash 的 AOT 宿主化（fourslash 运行时进 AotHost 的一个子命令，阶段 8 完成后补）。

### 8.3 触发策略摘要

- PR 关口求快：JIT 全量 + AOT 冒烟（linux）；AOT 全量一致性放 main/nightly——AOT 发布单平台 ~10 分钟级，三平台×全量 conformance 不适合 PR 节奏；
- 任何**阶段门禁签核**（[09-phases.md]）必须手动触发全矩阵（含三平台 aot-conformance 与确定性双跑），结果记 [11-progress.md] 签核日志；
- 上游 pin 升级 PR 额外强制：`fixtures regen -verify`、基线重同步核对、fourslash 重转换 diff 审阅。

### 8.4 AotHost：一致性套件的 AOT 宿主

xUnit 测试项目不做 AOT（主计划 §一.3）。AOT 轨的被测物是 `tests/Ts.Conformance.AotHost`：**无 xUnit 的控制台程序**，引用 `Ts.Compiler` + `Ts.TestUtil`（全部 harness/写出器/比较逻辑都在 TestUtil，xUnit 项目只是薄壳——这是把 harness 放 TestUtil 而非测试项目的原因），`PublishAot=true` 发布后以命令行复跑：

```
Ts.Conformance.AotHost run --suites conformance,compiler --mode local,submodule \
    [--verifiers errors,types,...] [--single-threaded] [--differential tokens,ast,...]
```

输出与 xUnit 轨完全同构（local 基线 + summary.json + 非零退出码），CI 直接 diff 两轨 summary 应全等。这就是 [07-aot-constraints.md]:496 所指「conformance 跑批器支持外部二进制模式：驱动已发布产物」的落地形态——发布产物即跑批器自身（内含被测编译器）；与「per-test 驱动外部 Ts.Cli 进程」的另一种解读的对齐见 §11 待澄清第 2 条。

### 8.5 e2e-compare：真实 CLI 对比

`tools/e2e-compare`（脚本 + 固定语料 lock）：对钉扎的一组真实项目（见 §9.3），分别跑 tsgo 与发布版 `Ts.Cli`（同参数：`--noEmit` / 全量 emit / `--build` / 增量二跑），逐字节 diff 全部产物（js/d.ts/map/tsbuildinfo/退出码/诊断 stdout 按 [01-architecture.md] 诊断输出章清洗版本号后比）。阶段 7 门禁「发布二进制编译真实大项目输出与 tsgo 全等，性能 ≤2×」由本脚本判定（时间测量用 hyperfine，见 §9.5）。

### 8.6 扩展冒烟：里程碑 2「官方扩展不改可用」的测量（裁决 R19）

不驱动 VS Code 本体，而是**按 `_extension` 真实启动路径**驱动发布二进制：脚本以官方扩展完全相同的方式 spawn `tscs --lsp`（对位 tsgo/_extension/src/client.ts:181 的 `args: ["--lsp", ...]`，stdio 传输），随后按 LSP 协议跑核心交互脚本——初始化握手（initialize/initialized）→ 打开文档（textDocument/didOpen）→ hover → 补全 → 诊断发布（等待 publishDiagnostics）——逐步断言响应形态。脚本落 `tools/extension-smoke/`，stdio 帧编解码复用 `Ts.LanguageService.Tests` 的 LSP 回放基础设施（§7.3 同一实现）。归属**阶段 10 门禁**新增一项；门禁编号归 [09-phases.md]（该文档补写中）。

### 9.1 工具与项目

- `tests/Ts.Benchmarks`：BenchmarkDotNet，微/中观基准（进程内）：scanner 全量 token 化、parser 单大文件、binder、checker 全量检查、printer emit；JIT 与 NativeAOT 双 toolchain（BenchmarkDotNet 的 NativeAot toolchain）各出一列；
- 端到端（跨实现对比只能在进程外做）：hyperfine 驱动 `Ts.Cli`（AOT 发布物）与 tsgo 二进制，冷启动与全量编译。

### 9.2 对照组：tsgo 本机构建

对照物 = **钉扎 commit 的 tsgo 在本机同构建配置下的产物**（`go build ./cmd/tsgo`，Go 工具链版本记入 `bench.lock.json`：tsgoCommit、goVersion、构建 flags、语料 lock）。不与官方发布二进制比（编译器版本/PGO 差异引噪音）。基准 runner 要求固定机型（专用 self-hosted 或至少固定 CI 机型标签），共享 runner 数据只作参考不入趋势库。

### 9.3 语料选择

| 档位 | 语料 | 用途 |
|---|---|---|
| 微 | `_submodules/TypeScript/src/compiler/checker.ts`（Strada 最大单文件） | scan/parse/bind 单文件微基准 |
| 中 | `_submodules/TypeScript/src/compiler`（tsconfig 全量 check） | checker/emit 中观；语料随 pin 天然锁定 |
| 大 | 钉死两项（裁决 R17）：① TypeScript 子模块 `src/compiler` 自举编译（pin 即子模块 commit）；② typescript-eslint 指定 commit 的 tarball（vendored，不用网络拉取）。lock 文件在 `tools/e2e-compare/corpus/`；更换语料须重签阶段 7 门禁 | e2e 冷启动/全量/增量/峰值内存 |

### 9.4 指标

1. **冷启动**：`tsc --version` 与「空项目 --noEmit」的 wall time（hyperfine, warmup 3 / runs 20）；
2. **全量检查耗时**：中、大语料 `--noEmit` wall time；并行（默认 4 checker）与 `--singleThreaded` 各一列；
3. **峰值内存**：max RSS（Linux `/usr/bin/time -v`、macOS `time -l`、Windows Job Object），JIT 轨附 `dotnet-counters` 的 GC heap 峰值作诊断列（不进对比表——tsgo 无对应物）；
4. 微基准的分配量（BenchmarkDotNet MemoryDiagnoser）作 GC-vs-arena 风险（[12-risks.md]）的观测哨。

### 9.5 趋势而非门禁

- 结果 JSON 逐次提交到 `bench-results` 分支（或 CI artifact + 汇总页），出趋势图；回归 >10% 自动在 PR/nightly 报告中标黄提醒，但**不 fail**——性能回归的处置是开 issue 进 [12-risks.md] 观察单，由阶段 10 集中调优（ServerGC、池化、剖析）；
- 与门禁的边界重申：主计划阶段 7 出口的「性能 ≤2× tsgo」是**一次性签核判据**，由 §8.5 的 e2e-compare 脚本在门禁评审时测量（固定机器、固定语料、中位数），不是 CI 常驻断言；本节的 BenchmarkDotNet 体系与它共享语料 lock 但互不为对方背书。

---

## 10. 与其他文档的接口备忘

- 本文 §2 的协议版本、§2.6 的 fixtures.lock 与 [04-codegen.md] 的「再生成命令总表」需互链（dumptool 是唯一 Go 侧生成器）；
- §4.4 vary-by 谓词依赖 [06-module-inventory.md] 的 tsoptions 条目按阶段 4 交付；
- §5 两垫片与 §6.3 size 字段已在 [12-risks.md] 有风险位（浮点/位置语义/字节保真），实现落地后回填「已缓解」状态；
- §8.2 矩阵中各 job 的门禁归属最终以 [09-phases.md] 的进入/退出条件表为准，本文提供机制、9 号文档提供时点。

## 11. 待澄清

1. **夹具存储通道**：§2.5 默认 Git LFS（估算压缩后数百 MB）。若组织策略不允许 LFS，备选「独立 fixtures 仓库 + lock 引用」或「CI artifact 缓存 + 本地 regen」，需在阶段 0 结束前定案（影响 CI 拓扑与开发者上手文档）。主计划未规定存储形态，此为本文新增决策点。——裁决 R16：本条决策列入阶段 0 出口 checklist（dumptool 骨架 patches/regen.sh/fixtures.lock.json 初版为阶段 0 交付物）；夹具位置另按裁决 R14 定为仓库根 `fixtures/`（§1.3、§2.5 已改）。
2. **「外部二进制模式」的精确含义**：[07-aot-constraints.md]:496 的表述可读作 (a) 发布 harness+编译器一体的 AotHost（本文 §8.4 方案），或 (b) 跑批器 per-test 驱动外部 `Ts.Cli` 进程。本文选 (a)（覆盖面完整：types/symbols 基线需要进程内 checker API，(b) 做不到），并以 §8.5 e2e-compare 覆盖 (b) 想验证的「真实 CLI 产物」维度。请 07 号文档维护者确认表述对齐。
3. **Ts.TestUtil 的 AOT 兼容要求**：主计划 §一.3「测试项目不 AOT」，但 §8.4 要求 AotHost（引用 Ts.TestUtil）可 AOT 发布 ⇒ Ts.TestUtil 须开 `IsAotCompatible` 且受 analyzer 约束（readable 写出器等本就按手写 writer 政策实现，无实质冲突）。建议 [07-aot-constraints.md] 的政策表将「测试基架」行细分为「xUnit 壳（不 AOT）/ TestUtil+AotHost（AOT 兼容）」。
4. ~~**fourslash 分级门禁的分母**~~——已关闭（裁决 R10，[09-phases.md] 已裁决）：分母取三段口径 289（顶层手写）+ 210（tests/manual/）+ 3,796（gen/）= 4,295；§7.5 第 4 条已同步该口径。
5. ~~**B 类夹具中 `value` 字段（TokenValue）**~~——已关闭（[09-phases.md] 已裁决）：TokenValue 计入阶段 2 门禁四元组，value 字段强制 100%（本文 §2.3.2 的建议被采纳；门禁编号见 [09-phases.md]，补写中）。
6. **语料计数的时点性**：本文所有计数（基线 49,321、testdata/tests 297、fourslash gen 3,796 / failing 459 / manual 177 / unparsed 2,570、submoduleAccepted 1,426 / Triaged 87）钉在 tsgo 坐标 A `168e7015…`；基线计数只含 Git 跟踪文件，不含本地 Finder `.DS_Store`。conformance 子模块语料总数在 `_submodules/TypeScript` 检出坐标 B 后由 [11-progress.md] 首次登记。
