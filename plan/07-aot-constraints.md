# 07 — Native AOT 约束全文

> **版本** v1.0 | **日期** 2026-07-11 | **状态** Draft
> 所属计划：typescript-go → C#/.NET 10 Native AOT 全量移植（主计划 §一.5「AOT 铁律」的展开文档）
> 交叉引用：[01-architecture.md]（AOT 发布管线/JSON 字节保真策略）、[04-codegen.md]（生成器族）、[05-conventions.md]（Go→C# 惯用法）、[08-testing-strategy.md]（AOT 二进制 CI）、[09-phases.md]（各阶段门禁）、[12-risks.md]（风险册）

本文档是移植全程的**第 0 天铁律**：每一条约束都由 MSBuild 属性 + Roslyn/ILC analyzer 在编译期强制，而不是靠人肉遵守。任何在 JIT 下能跑、在 AOT 下行为不同或无法编译的代码，都必须在写下它的那次构建里报错。判断标准只有一个：`dotnet publish /p:PublishAot=true` 的产物必须与 JIT 测试运行产出**逐字节相同**的编译结果（主计划 §四.2 AOT 双轨）。

---

## 目录

1. [禁用 API 清单](#1-禁用-api-清单)
2. [允许清单](#2-允许清单)
3. [Directory.Build.props 必需属性全文](#3-directorybuildprops-必需属性全文)
4. [System.Text.Json 政策](#4-systemtextjson-政策)
5. [内嵌资源政策（108 libs + 13 locale）](#5-内嵌资源政策)
6. [枚举 ToString 政策](#6-枚举-tostring-政策)
7. [InvariantGlobalization 专章](#7-invariantglobalization-专章)
8. [栈政策（LargeStackExecutor）](#8-栈政策largestackexecutor)
9. [泛型值类型实例化与二进制体积监控](#9-泛型值类型实例化与二进制体积监控)
10. [CI analyzer 门禁与本地验证](#10-ci-analyzer-门禁与本地验证)
11. [已知 .NET AOT 限制对本项目的影响评估](#11-已知-net-aot-限制对本项目的影响评估)
12. [待澄清](#12-待澄清)

---

## 1. 禁用 API 清单

原则（主计划 §一.5）：**禁反射（typeof / 枚举 ToString 除外）；System.Text.Json 仅源生成上下文，反射序列化器封禁。**

下表按「硬禁止」列出。所有条目在 `src/` 三个源项目内**一律禁止**，测试项目除注明外同样禁止（防止习惯扩散、保证 JIT/AOT 双轨行为一致）。执行机制列说明由哪个 analyzer/属性在编译期捕获；标注「BannedApi」者由 `Microsoft.CodeAnalysis.BannedApiAnalyzers` + 仓库根 `BannedSymbols.txt` 捕获（阶段 0 脚手架的一部分，见 [10-phase-0-scaffolding.md]）。

### 1.1 反射与动态代码生成

| 禁用 API | 禁止理由 | 替代 | 执行机制 |
|---|---|---|---|
| `System.Reflection.Emit.*` 全命名空间（`DynamicMethod`、`AssemblyBuilder`…） | AOT 无 JIT，运行时生成 IL 不可能 | 离线代码生成（[04-codegen.md]），产物入库 | IL3050（AOT analyzer）+ BannedApi |
| `Type.GetType(string)`、`Assembly.GetType(string)`（运行时字符串） | trimming 后类型可能不存在；返回 null 的失败在运行期才暴露 | `typeof(T)` | IL2057/IL2026（Trim analyzer）+ BannedApi |
| `Activator.CreateInstance(Type)`、`Activator.CreateInstance(assemblyName, typeName)` 及全部非泛型重载 | 运行时构造任意类型需保留全部构造器元数据 | `new T()`；泛型工厂用 `where T : new()` 约束 | IL2067/IL2072 + BannedApi |
| `Activator.CreateInstance<T>()` | AOT 下虽可编译，但与 `new T()` 完全同义且掩盖意图；统一禁掉便于审计 | `new T()` | BannedApi |
| `Type.MakeGenericType` / `MethodInfo.MakeGenericMethod` | 值类型实参的实例化必须编译期闭合，运行时组合在 AOT 下抛 `MissingRuntimeArtifactException` | 编译期写死全部实例化 | IL3050 + BannedApi |
| `System.Linq.Expressions.Expression` 全家（尤其 `Expression.Compile()`） | AOT 下 `Compile()` 落入**解释器回退**——不报错但慢一个数量级，且表达式树本身产生海量泛型实例化 | 普通委托 / 静态方法 | BannedApi（IL3050 只捕获部分路径，必须 BannedApi 兜底） |
| `dynamic` 关键字 / `Microsoft.CSharp.RuntimeBinder` | runtime binder 依赖 Reflection.Emit | 静态类型；多态用继承（AST/Type 层次本来就是密封类树） | BannedApi（binder 类型）+ 代码评审；tsgo 无任何对应物，出现即移植错误 |
| `Delegate.DynamicInvoke` | 反射调用，慢且掩盖签名错误 | 强类型委托 `Invoke` | BannedApi |
| `MethodInfo.Invoke` / `PropertyInfo.GetValue/SetValue` / `FieldInfo.GetValue/SetValue` | tsoptions 的反射 merge/遍历（declscompiler.go:1203-1246、parsinghelpers.go:608-609、showconfig.go:169-170）是 Go 侧唯一的产品级反射；C# 侧以生成的 per-field switch 取代（主计划 §一.5，[04-codegen.md]） | `tools/generator` 生成的 ~150 字段 switch（`CompilerOptions.ForEachValue` / `MergeFrom` / `HasChanges`） | IL2075 族 + BannedApi |
| `Type.GetMethods/GetProperties/GetFields/GetCustomAttributes` 等元数据枚举 | trimming 后结果不完整，且是隐性反射序列化温床（Go 侧 lsproto/structcodec.go:34-160 的反射解码器就是这类——**不移植**，见 §4.2） | 生成代码 | IL2070 族 + BannedApi |

### 1.2 程序集加载与序列化

| 禁用 API | 禁止理由 | 替代 | 执行机制 |
|---|---|---|---|
| `Assembly.LoadFrom` / `LoadFile` / `Load(byte[])`、`AssemblyLoadContext` 动态加载 | AOT 单文件产物没有可加载的外部程序集概念 | 无——本项目零插件模型（tsgo 亦无） | IL2026 + BannedApi |
| STJ 反射序列化器：`JsonSerializer.Serialize/Deserialize` 所有**不带** `JsonTypeInfo`/`JsonSerializerContext` 的重载；`DefaultJsonTypeInfoResolver` | 反射解析契约在 AOT 下抛异常或静默丢字段 | 源生成上下文或手写 `Utf8JsonReader/Writer`（§4） | **`JsonSerializerIsReflectionEnabledByDefault=false`**（运行期一票否决）+ IL2026/IL3050 + BannedApi |
| `BinaryFormatter`（.NET 9 起已移除）、`XmlSerializer`、`DataContractSerializer`、`DataContractJsonSerializer` | 反射序列化 + 动态代码生成 | 不需要——本项目全部线缆格式为 JSON/msgpack 手写 | BannedApi |
| `Assembly.Location`、`Assembly.CodeBase` | 单文件/AOT 下返回空字符串（EnableSingleFileAnalyzer 捕获场景） | `AppContext.BaseDirectory`、`Environment.ProcessPath` | IL3000（SingleFile analyzer）|

### 1.3 其他 AOT 陷阱

| 禁用 API | 禁止理由 | 替代 | 执行机制 |
|---|---|---|---|
| `RegexOptions.Compiled` | AOT 下静默降级为解释执行，性能预期落空 | `[GeneratedRegex]` 源生成（scanner/正则重扫描是手写状态机，产品代码正则本就极少） | BannedApi（枚举值无法 Banned，用 analyzer 规则 SYSLIB1045 提示 + 代码评审清单） |
| `Marshal.PtrToStructure` / `StructureToPtr` / `GetDelegateForFunctionPointer`（非泛型） | 运行时 marshalling 依赖类型元数据 | blittable struct + `[LibraryImport]` 源生成 marshalling（fswatch 阶段才可能用到，§11.3） | AOT analyzer + BannedApi |
| 内建 COM / WinRT interop | NativeAOT 不支持（`BuiltInComInteropSupport` 默认 false，保持） | 不需要 | 编译期错误 |
| `Thread.Sleep`+轮询之外——`Task.Run` / `ThreadPool.QueueUserWorkItem` **在 Ts.Compiler 递归核心内** | 线程池线程栈 1MB，parse/bind/check 递归必炸（§8）；这是「受限」而非全局禁止：Ts.LanguageService 的 IO/调度层可用 | `LargeStackExecutor` / `WorkGroup` | BannedApi 按项目分级（Ts.Compiler 的 BannedSymbols 含 Task.Run）+ 代码评审 |
| `Environment.StackTrace`、`new StackTrace()` 用于逻辑判断 | AOT 下帧信息可能缺方法名（未启用完整 stack trace data 时） | 仅诊断输出可用，禁止参与控制流 | 代码评审 |
| culture 敏感字符串/格式化 API（详表见 §7.3） | InvariantGlobalization + 字节保真 | ordinal 比较 + `StringUtil.ToLowerJS/ToUpperJS` + `CultureInfo.InvariantCulture` 显式传参 | CA1304/1305/1307/1309/1310 = error |

> **BannedSymbols.txt 是本清单的机器可读镜像**，两者同步维护；清单变更须同 PR 修改两处。文件放仓库根，经 Directory.Build.props 的 `<AdditionalFiles>` 注入所有项目。

---

## 2. 允许清单

以下 API/模式已核验为 AOT 安全，移植时放心使用（对照 Go 侧惯用法映射见 [05-conventions.md]）：

| 允许项 | 说明 / 使用位置 |
|---|---|
| `typeof(T)`、`is` / `as`、`GetType()` 用于相等性比较 | 类型 switch 直译 Go type switch（[05-conventions.md] 类型 switch 模式）；`GetType()` 仅比较不反射成员 |
| **编译期闭合的静态泛型** | `OrderedMap<K,V>`、`LinkStore<K,V>`、`Dictionary<,>` 等一切类型实参在源码里写死的泛型；ILC 全量预编译（体积影响见 §9） |
| `where T : new()` + `new T()` | NodeFactory / 链接类工厂 |
| **枚举 ToString / `Enum.GetValues<T>()` / `Enum.IsDefined<T>()`（泛型版）** | AOT 保留静态可达枚举的名字表；但基线承重路径必须走生成 stringer，见 §6 |
| `static ReadOnlySpan<byte> X => new byte[] {...}` / `static ReadOnlySpan<char>` 属性 | 编译为 **RVA 静态数据**，零分配零 cctor；Unicode 标识符表、js_case 表的目标形态（[04-codegen.md] 第 5 族；Go 源：stringutil/identifier_parts_generated.go:12-1058） |
| `readonly record struct` | 结构键缓存（主计划 §一.6）：编译器合成 `Equals/GetHashCode/IEquatable`，AOT 安全零装箱 |
| `System.Threading.Lock`、`ReaderWriterLockSlim`、`Interlocked`、`Volatile` | Go sync 原语映射表（主计划 §一.5 依赖映射） |
| `System.Threading.Channels` | Go channel 映射 |
| `System.IO.Hashing.XxHash128/XxHash3` | 官方 AOT 兼容包；xxh3 保真（主计划 §一.6） |
| `[GeneratedRegex]` | 源生成正则，AOT 下编译为代码 |
| `[LibraryImport]` + `DllImport`（静态入口点、blittable 签名） | AOT 支持 P/Invoke；fswatch 阶段备用（§11.3） |
| `NativeLibrary.Load/TryGetExport`（**固定字符串**库名/符号名） | macOS FSEvents 等 framework 直调的合法路径（§11.3）；库名不得来自运行时输入 |
| STJ 源生成：`JsonSerializerContext` 派生 + `[JsonSerializable]`；`Utf8JsonReader/Utf8JsonWriter` 手写 | §4 |
| `Assembly.GetManifestResourceStream/Names`（**常量 LogicalName**） | 内嵌资源读取（§5）；AOT + 单文件下均安全 |
| `GZipStream`（System.IO.Compression） | locale json.gz 解压（§5.3）；纯托管，AOT 安全 |
| `stackalloc`、`ArrayPool<T>.Shared`、`string.Create` | 热路径零分配惯用法 |
| `ReferenceEqualityComparer.Instance` | LinkStore 的 Go 指针键语义（主计划 §一.6） |
| 函数指针 `delegate*` 与 static lambda | 性能敏感回调（printer pipeline 钩子等）；非必须，允许 |
| `Lazy<T>` / `LazyInitializer` | Go `sync.OnceValue` 映射（bundled.go:34、loc_generated.go 的惰性 locale 加载） |
| `[ModuleInitializer]` | 极少数需要确定性启动次序的初始化（§11.2）；每次使用须在代码注释中说明对应 Go init() 依据 |

---

## 3. Directory.Build.props 必需属性全文

### 3.1 分层结构

```
typescript-csharp/
  Directory.Build.props          # 根：全项目公共（含测试）
  BannedSymbols.txt              # §1 的机器可读镜像
  .editorconfig                  # analyzer 严重性（§7.4、§10.1）
  src/
    Directory.Build.props        # 源项目追加：IsAotCompatible 等
  tests/
    Directory.Build.props        # 测试项目追加：关闭 AOT 兼容标记（测试不 AOT，主计划 §一.3）
  src/Ts.Cli/Ts.Cli.csproj       # PublishAot 与发布专属属性
```

分层理由：`IsAotCompatible` 只对类库有意义且会连带 `IsTrimmable`；测试项目引用 xunit.v3 等不 AOT 兼容的包，若在根上强制会产生无法修复的告警。**但 InvariantGlobalization 与 analyzer 必须覆盖测试项目**——JIT 双轨测试若跑在非 invariant 模式下，字符串行为会与 AOT 产物分叉，基线 diff 失去意义。

### 3.2 根 `Directory.Build.props` 全文

```xml
<Project>
  <PropertyGroup>
    <!-- 语言与目标框架 -->
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>

    <!-- 全球化：全仓库一律 invariant（§7） -->
    <InvariantGlobalization>true</InvariantGlobalization>

    <!-- STJ 反射序列化器一票否决（§4.1） -->
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>

    <!-- AOT/Trim/单文件三 analyzer 全开（含测试项目：测试代码也会被拷进产品的诱惑必须掐死） -->
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>

    <!-- 告警即错误：IL/CA 违规不允许进主干 -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest</AnalysisLevel>
    <AnalysisModeGlobalization>All</AnalysisModeGlobalization>
    <CodeAnalysisTreatWarningsAsErrors>true</CodeAnalysisTreatWarningsAsErrors>

    <!-- 确定性构建与集中包管理 -->
    <Deterministic>true</Deterministic>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

### 3.3 `src/Directory.Build.props` 全文

```xml
<Project>
  <Import Project="$(MSBuildThisFileDirectory)../Directory.Build.props" />
  <PropertyGroup>
    <!-- 类库 AOT 兼容承诺：连带 IsTrimmable=true，并让下游 publish 时分析本程序集 -->
    <IsAotCompatible>true</IsAotCompatible>
    <RootNamespace>Ts</RootNamespace>
  </PropertyGroup>
</Project>
```

### 3.4 `tests/Directory.Build.props` 全文

```xml
<Project>
  <Import Project="$(MSBuildThisFileDirectory)../Directory.Build.props" />
  <PropertyGroup>
    <IsAotCompatible>false</IsAotCompatible>
    <!-- 测试保留三 analyzer 与 InvariantGlobalization（继承自根），保证 JIT 双轨与 AOT 行为一致 -->
  </PropertyGroup>
</Project>
```

### 3.5 `src/Ts.Cli/Ts.Cli.csproj` 发布相关属性

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <PublishAot>true</PublishAot>
  <StackTraceSupport>true</StackTraceSupport>
  <!-- ServerGC/OptimizationPreference 为阶段 10 调优旋钮，初始不设（见 §12 待澄清第 5 条） -->
</PropertyGroup>
```

### 3.6 逐属性理由

| 属性 | 值 | 位置 | 理由 |
|---|---|---|---|
| `TargetFramework` | `net10.0` | 根 | 主计划钦定；.NET 10 的 NativeAOT 是本项目发布形态，`System.Threading.Lock` 等新原语需要 ≥net9.0 |
| `LangVersion` | `14.0` | 根 | 显式钉住，禁 preview——生成代码（[04-codegen.md]）必须在固定语言版本下可重现编译 |
| `Nullable` | `enable` | 根 | Go 的 nil 纪律映射为编译期空检查：`!= nil` → `!= null` 的逐点直译（主计划 §一.1；[05-conventions.md]）在 NRT 下由编译器审计，漏掉的 null 分支直接报警 |
| `ImplicitUsings` | `disable` | 根 | 两个硬理由：① 本项目类型名与 BCL 大面积撞名——`Ts.Checker` 命名空间里的 `Type`（checker 24 类型树的根，主计划 §一.2）与 `System.Type`、`Ts.Ast.Node` 与若干 BCL `Node`，隐式 `using System;` 会让全 checker 的 `Type` 变成二义性错误或更糟（静默解析到 System.Type）；② 1:1 直译要求文件级依赖显式可见，对照 Go 文件的 import 块 |
| `InvariantGlobalization` | `true` | 根（含测试） | §7 专章；写在根而非 CLI 是为了 JIT 测试与 AOT 产物同一行为 |
| `JsonSerializerIsReflectionEnabledByDefault` | `false` | 根 | 让任何漏网的无 context `JsonSerializer.Serialize` 调用在**运行期第一时间**抛 `InvalidOperationException`（JIT 测试即暴露），配合 IL3050 编译期双保险（§4.1） |
| `PublishAot` | `true` | **仅 Ts.Cli.csproj** | 发布形态属性。不放根：测试项目/生成器工具不 AOT（主计划 §一.3「测试项目不 AOT」）；类库不需要它——类库靠 `IsAotCompatible` 表达承诺 |
| `IsAotCompatible` | `true` | src 层 | 类库的 AOT 契约：隐含 `IsTrimmable=true` + 三 analyzer，并在包/程序集元数据上打标，publish 时 ILC 才会对其执行完整分析 |
| `EnableTrimAnalyzer` | `true` | 根 | IL2xxx：捕获 trimming 不可达元数据的反射访问。虽被 `IsAotCompatible` 隐含，仍显式写出——测试项目不设 IsAotCompatible 但同样要 analyzer |
| `EnableAotAnalyzer` | `true` | 根 | IL3050 族：捕获 RequiresDynamicCode 路径（Reflection.Emit、MakeGenericType 运行时组合等） |
| `EnableSingleFileAnalyzer` | `true` | 根 | IL3000 族：捕获 `Assembly.Location` 等单文件陷阱；AOT 产物即单文件 |
| `TreatWarningsAsErrors` | `true` | 根 | 门禁不留「稍后修」空间；IL/CA 全部升 error |
| `AnalysisModeGlobalization` | `All` | 根 | CA1304/1305/1307/1309/1310 默认不开启，此开关把 globalization 类别全量激活，再由 .editorconfig 定 severity（§7.4） |
| `Deterministic` | `true` | 根 | 逐字节可重现构建，配合基线 diff 文化 |
| `ManagePackageVersionsCentrally` | `true` | 根 | Directory.Packages.props 集中版本（主计划 §一.3） |
| `StackTraceSupport` | `true`（默认） | Ts.Cli | 保留异常栈帧名——crash 报告对 conformance 排错至关重要；体积代价见 §9 监控 |

---

## 4. System.Text.Json 政策

### 4.1 总原则

1. **反射序列化器封禁**：三重防线——编译期 IL3050/IL2026、运行期 `JsonSerializerIsReflectionEnabledByDefault=false`、BannedApi 禁无 context 重载。
2. **每个序列化面一个独立的 `JsonSerializerContext`**。禁止全仓库共享巨型 context：① 编译时间与生成代码体积按契约数线性膨胀，巨 context 拖垮增量编译；② 面与面选项不同（LSP 需要 camelCase + 忽略 null，api 面不同），选项在 `[JsonSourceGenerationOptions]` 上按 context 配置。
3. **进入逐字节 diff 门禁的输出一律手写 `Utf8JsonWriter`**（判据见 §4.3）。手写 reader/writer 不属于「反射序列化」，是允许清单成员。
4. 所有需要非 ASCII 直通/宽松转义的手写 writer 统一用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`（主计划 §一.5：.NET 默认编码器转义 `<` `>` `&` 及非 ASCII，会破坏与 Go 输出的字节保真）。

### 4.2 序列化面清单（逐面：Go 证据 → C# 方案）

| # | 面 | Go 侧证据 | C# 方案 | 阶段 |
|---|---|---|---|---|
| 1 | **tsconfig / jsonc 读取** | tsoptions/tsconfigparsing.go:291、704、957——用 `parser.ParseSourceFile` 把 tsconfig 当 JSONC 源文件解析，**根本不经过 JSON 库** | 随 parser 移植，**无 JSON 序列化器参与**。此面在 STJ 政策中的实际内容只剩两块：`--showConfig` 写出（tsoptions/showconfig.go:165-185 经反射遍历 CompilerOptions 构建 OrderedMap，C# 侧换生成 switch，见 §1.1）与 `core.go:523` 的 `json.MarshalIndent`——写出走**手写 Utf8JsonWriter**（缩进版式逐字对齐 Go 输出，进基线） | 4 |
| 2 | **.tsbuildinfo 读写** | execute/incremental/buildInfo.go（全文件 630 行；`BuildInfo` 19 字段声明序 + `json:"...,omitzero"`，如 buildInfo.go:73、83；7 种多态形状 buildInfo.go:34-448）；写出经 incremental/snapshottobuildinfo.go | **手写 `Utf8JsonReader`/`Utf8JsonWriter`**。字节保真要求全文见主计划 §一.5：UnsafeRelaxedJsonEscaping、单行紧凑无尾换行、键序=Go struct 声明序、omitzero（省略 false/0/""/nil）、多态形状逐个手写、控制字符短转义、无效 UTF-8→U+FFFD、version 精确等于 `core.Version()`（core/version.go:8 `"7.1.0-dev"`）。**不建 context**——STJ 源生成的 `JsonIgnoreCondition` 与 Go omitzero 语义不同构（Tristate 零值、nil-slice vs 空 slice 的区分会丢失） | 7 |
| 3 | **LSP 协议** | lsp/lsproto/lsp_generated.go（17k 行，由 metaModel.json 生成）+ jsonrpc.go；lsproto/structcodec.go:11-160 是**反射驱动**的解码器（`reflect.Type -> structSpec` 缓存） | `generate-lsp.ts`（[04-codegen.md] 第 4 族）产出：record 类型 + **`LspJsonContext : JsonSerializerContext`**（源生成）+ OR/union 类型手写 `JsonConverter`。**structcodec.go 的反射机制不移植**——它解决的「缺字段=null、per-type spec」问题由源生成契约天然覆盖 | 8 |
| 4 | **package.json** | packagejson/packagejson.go:118 `json.Unmarshal(data, &f, json.AllowDuplicateNames(true))`；jsonvalue.go 的保序多态 `JsonValue`；exportsorimports.go 的 exports/imports 树 | **手写 `Utf8JsonReader` 构建 `JsonValue` 树**（判据 §4.3-②：保序 + any 多态 + 重复键宽容）。`Utf8JsonReader` 天然不拒绝重复键，后写覆盖先写，与 Go `AllowDuplicateNames(true)` 语义一致——但此点须在阶段 4 用夹具测试钉死（重复键 package.json 样例） | 4 |
| 5 | **api JSON-RPC** | api/protocol_jsonrpc.go、conn.go、proto.go 用 encoding/json 系；api/protocol_msgpack.go:32-102 是**手写 msgpack 字节码器**（与 JSON 无关，直译即可，主计划阶段 9「手写 msgpack 帧直译，零依赖」） | **`ApiJsonContext : JsonSerializerContext`**（请求/响应封套 + proto 类型）；msgpack 面手写字节操作直译 | 9 |
| 6 | **source map (.js.map)** | sourcemap/generator.go:340 `json.Marshal(gen.RawSourceMap())` | **手写 `Utf8JsonWriter`**——`.js.map` 进阶段 6 的 100% 字节匹配门禁；RawSourceMap 仅 ~7 字段，手写十几行；注意 go-json-experiment 默认不转义非 ASCII（sources 路径可含非 ASCII），必须 UnsafeRelaxedJsonEscaping | 6 |
| 7 | **locale 诊断资源** | diagnostics/loc_generated.go:50-60 `gzip.NewReader` + `json.UnmarshalRead` → `map[Key]string` | `GZipStream` + **手写 `Utf8JsonReader`** 读平坦 string→string 映射（形状平凡，~20 行，不值得开 context；固定为手写） | 9 |
| 8 | **ATA / typings 发现**（语言服务） | project/ata/ata.go、discovertypings.go | **`AtaJsonContext`**（形状机械、无字节保真要求） | 8 |
| 9 | **测试基架**（readable buildinfo、fourslash 状态基线等） | execute/tsctests/readablebuildinfo.go、fourslash/statebaseline.go 等 | 测试项目不 AOT，但同样禁反射序列化器（根 props 覆盖）；readable-baseline 写出器为手写 writer（2 空格缩进 + `size=` 紧凑文本字节长，主计划 §一.5） | 各阶段 |

> 汇总：**JsonSerializerContext 共 3 个**——`LspJsonContext`、`ApiJsonContext`、`AtaJsonContext`；其余序列化面全部手写 `Utf8JsonReader/Writer`。每个 context 一个独立 partial class 文件，与其协议生成代码同目录。

### 4.3 手写 Utf8JsonReader/Writer 的适用判据

满足任意一条即必须手写，不得用 context：

1. **输出进入逐字节 diff 门禁或持久化文件**（.tsbuildinfo、.js.map、--showConfig、readable-baseline）。理由：字节保真需要控制键序、omitzero 逐字段语义、非 ASCII 直通、缩进版式——源生成器的旋钮粒度不够（如 `DefaultIgnoreCondition` 是全局的，无法表达「此字段 0 省略、彼字段 0 保留」；Go 的 omitzero 恰恰是逐字段标注）。
2. **输入形状为保序 map / any 多态 / 宽容语法**（package.json 的 JsonValue、OrderedMap[string, any]）。STJ 契约模型以静态 POCO 为中心，动态树手写更短且行为可证。
3. **形状平凡到 context 是杀鸡牛刀**（locale 的 string→string 平坦 map）。

反向判据（用 context）：schema 大而机械、字段数以百计、由生成器产出、无字节保真要求——LSP（271 个请求/通知类型）、api proto、ATA。

---

## 5. 内嵌资源政策

### 5.1 资产盘点

| 资产 | 数量 | Go 侧机制 | 体积特征 |
|---|---|---|---|
| `lib.*.d.ts` | **108 个**（已核验 internal/bundled/libs/ 计数） | `//go:embed`（bundled/embed_generated.go），以 string map `embeddedContents` 常驻；`bundled:///` scheme 的 wrappedFS 覆盖层（embed.go:15-80） | 明文 d.ts，合计 ~6MB 量级 |
| locale 诊断表 | **13 个** `*.json.gz`（cs-CZ、de-DE、es-ES、fr-FR、it-IT、ja-JP、ko-KR、pl-PL、pt-BR、ru-RU、tr-TR、zh-CN、zh-TW，internal/diagnostics/loc/） | `//go:embed loc/xx-XX.json.gz`（loc_generated.go:62-146）+ 惰性 gzip 解压（loc_generated.go:50-60） | 压缩存放，按需解压 |

### 5.2 组织：csproj 与 LogicalName 规范

资产从子模块（`_submodules/TypeScript`）经拷贝脚本同步进 `src/Ts.Compiler/Bundled/`（[04-codegen.md] 第 3 族：「拷贝脚本 + EmbeddedResource glob」），**入库提交**（与生成代码同哲学：构建不依赖子模块存在）。

```xml
<!-- src/Ts.Compiler/Ts.Compiler.csproj -->
<ItemGroup>
  <EmbeddedResource Include="Bundled/libs/**/*.d.ts"
                    LogicalName="bundled/libs/%(Filename)%(Extension)" />
  <EmbeddedResource Include="Diagnostics/loc/**/*.json.gz"
                    LogicalName="loc/%(Filename)%(Extension)" />
</ItemGroup>
```

规则：

1. **LogicalName 显式指定**、稳定、全小写路径风格，与 `bundled:///libs/<name>` 的查找键后缀严格对应——不能依赖 MSBuild 默认的 `RootNamespace.目录.文件名` 命名（目录改名即静默破坏查找）。
2. lib 文件名清单同时生成 `Bundled/LibNames.g.cs`（对应 bundled/libs_generated.go 的 `LibNames`），供 `GetAccessibleEntries` 枚举（embed.go:72-80 的目录列举语义）与 tsoptions 的 lib 校验使用——**运行时不得用 `GetManifestResourceNames()` 扫描来发现 lib**（扫描结果排序无保证，且把资源命名变成了运行时契约）；`GetManifestResourceNames` 仅允许出现在测试里做「清单=实际资源」的一致性断言。
3. 上游升级流程：更新子模块 pin → 跑拷贝脚本 → diff 审查 → 重新生成 LibNames.g.cs（所有权与节奏见 [00-overview.md]、[04-codegen.md]）。

### 5.3 读取 API 与缓存

```csharp
// Ts.Compiler / Bundled/BundledFs.cs（对应 bundled/embed.go 的 wrappedFS）
private static string ReadEmbedded(string logicalName)
{
    using Stream s = typeof(BundledFs).Assembly.GetManifestResourceStream(logicalName)
        ?? throw new InvalidOperationException($"missing embedded resource: {logicalName}");
    using var r = new StreamReader(s, Encoding.UTF8);
    return r.ReadToEnd();
}
```

- **读取 API 钦定为 `Assembly.GetManifestResourceStream`（常量 LogicalName）**：AOT + 单文件下资源节随镜像映射，安全且零拷贝打开；被 §2 允许清单收录。
- lib 内容缓存：`ConcurrentDictionary<string, string>` 惰性填充（Go 侧 go:embed string 是启动即常驻；C# 惰性化是刻意改进，避免 108 个文件 ~6MB 的 UTF-16 字符串在不需要时常驻——行为不可观察，允许）。`bundled:///` scheme、`IsBundled`、`LibPath` 与 wrappedFS 的全部覆盖语义（FileExists/ReadFile/DirectoryExists/GetAccessibleEntries，embed.go:48-80）原样直译。
- locale：`Lazy<Dictionary<string, string>>[13]`，first-touch 时 `GetManifestResourceStream` → `GZipStream` → 手写 `Utf8JsonReader`（§4.2-7）；对应 loc_generated.go 的 `sync.OnceValue` 惰性模式。**存 .gz 不解压入程序集**——与 Go 相同，控制镜像体积。
- locale 协商：Go 用 `golang.org/x/text/language.NewMatcher`（loc_generated.go:15-30，固定 14 个 tag）做 BCP-47 匹配。InvariantGlobalization 下 `CultureInfo` 不可用于匹配，需手写微型 matcher（固定表上的精确匹配 + 语言前缀回退，几十行）——列入阶段 9 工作项与 §12 待澄清第 2 条。

---

## 6. 枚举 ToString 政策

AOT 事实：NativeAOT **保留**静态可达枚举的名字表，`enum.ToString()`、`Enum.GetValues<T>()`、`Enum.Parse<T>()` 都能工作——所以这不是 AOT 正确性问题，而是**基线保真**问题：

1. C# 枚举成员名是 PascalCase 惯例命名（主计划风格决策），与 Go stringer 输出的文本**不同**；`[Flags]` 组合值的 `ToString()` 格式（`"A, B"`）与 Go 自定义 stringer 也不同。
2. 因此规则是：**任何进入基线、诊断文本、协议线缆、`--showConfig`/trace 输出的枚举字符串，必须走生成的 `ToDisplayString()` switch**（[04-codegen.md] 第 6 族 generate-stringers；对应 Go 的 `*_stringer_generated.go`：core/scriptkind、tristate、modulekind、languagevariant、diagnostics/stringer_generated.go 等）。生成器只为「基线承重」枚举产出 stringer——清单由 [06-module-inventory.md] 的危险点列维护。
3. `enum.ToString()` 仅允许用于：调试日志、异常消息、断言文本——即任何不参与逐字节 diff 的输出。
4. `Enum.Parse` 系禁止用于协议/选项解析：tsoptions 的枚举值表在 Go 里本就是显式 map（tsoptions/enummaps.go），直译为 `Dictionary<string, T>(StringComparer.Ordinal)`，不需要也不允许反射 parse。
5. switch 式 stringer 是 AOT 最优形态：无元数据依赖、可内联、trimming 后零残留。

---

## 7. InvariantGlobalization 专章

### 7.1 为什么必须开

`<InvariantGlobalization>true</InvariantGlobalization>` 把整个进程钉死在 invariant culture：没有 ICU/NLS 加载，所有 culture 表现为 invariant。四个理由，每个都独立成立：

1. **JS/TS 语义本来就是 ordinal 的**。TypeScript 编译器的一切字符串比较——标识符、文件路径、模块名、诊断排序——在 tsgo 中都是码元序或 Go unicode 简单映射（stringutil/compare.go），没有任何 culture 依赖。开启 invariant 不是「牺牲」，而是让平台默认值与语义对齐，任何漏写 `StringComparison` 的调用点不再产生 culture 相关的隐性 bug。
2. **stringutil 自带完整的 JS 精确大小写表**。js_case_generated.go 钉死 Unicode 15.1.0（文件头注释：「Derived from the @unicode/unicode-15.1.0 package」），包含 ECMAScript 默认大小写所需的多码点映射 + Final_Sigma 上下文映射；js_case.go:9-70 的 `ToLowerJS/ToUpperJS` 精确复刻 `String.prototype.toLowerCase/toUpperCase`（含孤立代理项保真）。**这套移植表是全仓库唯一合法的大小写转换路径**——OS ICU 版本各异，任何依赖它的转换都会让三平台基线 diff 不稳定。
3. **消除部署依赖与体积**：非 invariant 的 AOT 产物在 Linux 上要求目标机有 libicu（或附带 ICU 数据 ~10MB+）。invariant 模式产物零外部依赖。
4. **确定性是验收货币的前提**：主计划 §四以逐字节 diff 为唯一验收标准，三平台 CI 必须对同一输入产出同一字节流。

### 7.2 合法的大小写/比较路径（全集）

| 需求 | 唯一合法路径 | Go 源依据 |
|---|---|---|
| JS 可观察大小写（`Lowercase<S>`/`Uppercase<S>` intrinsic、completion 匹配等） | `StringUtil.ToLowerJS` / `ToUpperJS`（移植 js_case.go + js_case_generated.go 表） | js_case.go:9、44 |
| 不区分大小写的文件名/路径比较 | 移植 compare.go 的 `EquateStringsCaseInsensitive` / `CompareStringsCaseInsensitive`（unicode.ToLower 简单映射逐 rune，compare.go:50-51） | compare.go:44-60 |
| UI 排序用 case-insensitive 比较（ESLint 兼容，用 toLowerCase） | 移植 `CompareStringsCaseInsensitiveEslintCompatible`（compare.go:111-126） | compare.go:125 |
| 精确相等/排序 | `string.Equals`/`CompareOrdinal`/`StringComparison.Ordinal` | compare.go 码元比较 |
| ASCII 快路径 | 移植 toLowerASCII/toUpperASCII 快路径（js_case.go:10-12） | js_case.go:10 |

**注意**：不得用 `StringComparison.OrdinalIgnoreCase` 直接顶替 compare.go 的 case-insensitive 系——两者的简单映射表在绝大多数码点一致，但版本锚点不同（.NET 内置表 vs Unicode 15.1.0 钉死表）。凡 Go 调 `stringutil.EquateStringsCaseInsensitive` 的移植点必须调用移植版；只有在逐案证明不可观察（如纯内部 map 键归一化且两端同函数）时方可换用，并在代码注释标注（审计规则同 [05-conventions.md] 的 map 迭代序审计）。

### 7.3 禁止的 API（globalization 子清单）

| 禁用 | 理由 / 替代 |
|---|---|
| `string.ToLower()` / `ToUpper()`（无参） | 即使 invariant 模式下行为等于 Invariant 版，仍禁止——无参版无法被审计出「这里到底要 JS 语义还是内部归一化」。替代：§7.2 表 |
| `string.ToLowerInvariant()` / `ToUpperInvariant()` | 同上：.NET invariant 表 ≠ Unicode 15.1.0 钉死表 ≠ JS 语义。仅当逐案标注证明等价时豁免（预期豁免数：0） |
| `char.ToLower(char)` / `char.ToUpper(char)`（culture 版） | 用 `char.ToLowerInvariant` 仅限 ASCII 已证明区间，或走 §7.2 |
| `CultureInfo.CurrentCulture` / `CurrentUICulture` 参与任何逻辑 | invariant 下恒为 invariant，读它即撒谎；locale 协商走手写 matcher（§5.3） |
| `string.Compare(a, b)`、`a.CompareTo(b)`、`IndexOf(string)`、`StartsWith(string)`、`EndsWith(string)`、`Equals(string)` 等**不带 `StringComparison`** 的重载 | CA1307/CA1310 捕获；一律显式 `StringComparison.Ordinal`（或移植的比较器） |
| `int.Parse` / `double.Parse` / `ToString` 等**不带 `IFormatProvider`** 的数字/日期格式化 | CA1305 捕获。数字→字符串的 JS 语义一律走 JsNum 层（主计划阶段 1 门禁）；其余显式 `CultureInfo.InvariantCulture` |
| `RegexOptions.IgnoreCase` 不带 `RegexOptions.CultureInvariant` | IgnoreCase 默认用 current culture 折叠；产品代码正则本就少，逐个审 |
| `CompareInfo` / `TextInfo` / `StringComparer.CurrentCulture*` / `StringComparer.InvariantCulture*` | 一律 `StringComparer.Ordinal(IgnoreCase)` 或移植比较器 |

### 7.4 analyzer 强制

`AnalysisModeGlobalization=All`（§3.2）激活类别，`.editorconfig` 钉 severity：

```ini
# .editorconfig（仓库根）
dotnet_diagnostic.CA1304.severity = error   # 指定 CultureInfo
dotnet_diagnostic.CA1305.severity = error   # 指定 IFormatProvider
dotnet_diagnostic.CA1307.severity = error   # 指定 StringComparison（明确性）
dotnet_diagnostic.CA1309.severity = error   # 使用 ordinal StringComparison
dotnet_diagnostic.CA1310.severity = error   # 指定 StringComparison（正确性）
dotnet_diagnostic.CA1311.severity = error   # 指定 culture 或用 invariant 版
```

配合 `CodeAnalysisTreatWarningsAsErrors=true`，任一违规即构建失败。CA 管不到的（`ToLowerInvariant` 的钉表差异、OrdinalIgnoreCase 顶替禁令）由 BannedSymbols.txt + 代码评审清单兜底。

---

## 8. 栈政策（LargeStackExecutor）

### 8.1 问题（主计划「0 号风险」）

Go 协程栈按需增长（默认可长到 1GB；tsgo 还提供 `TS_GO_DEBUG_STACK_LIMIT` 环境变量经 `runtime/debug.SetMaxStack` 收紧，core/core.go:24-34），checker/parser 的递归深度由**用户输入**决定（深嵌套表达式、巨型 union、条件类型展开），上不封顶。.NET 线程栈在创建时固定：主线程 ~1-8MB（平台相关）、线程池线程 ~1MB——conformance 语料里的深递归测例必然 `StackOverflowException`，且 .NET 的 SO **不可捕获、直接进程崩溃**。原版 tsc.js 靠 node `--stack-size` 解决同一问题。

### 8.2 对策

`Ts.Core.LargeStackExecutor`（Ts.Compiler 内，阶段 0 交付）：所有编译器递归核心一律跑在 **maxStackSize = 64MB（67,108,864 字节）的专用线程**上。

```csharp
public static class LargeStackExecutor
{
    public const int DefaultStackSize = 64 << 20;  // 主计划 §一.5 钦定 64MB

    public static T Run<T>(Func<T> fn) { /* new Thread(..., DefaultStackSize){ IsBackground = true } + join + 异常回传 */ }
    public static Thread NewWorkerThread(ThreadStart start) => new Thread(start, DefaultStackSize) { IsBackground = true };
}
```

`Thread(ThreadStart, maxStackSize)` 在三平台均生效（Windows CreateThread 栈保留、Unix pthread_attr_setstacksize）——这是 .NET 里**唯一可移植**的大栈手段。

**WorkGroup 的两个实现共用它**（主计划 §一.5）：

- 并行版（对应 core/workgroup.go:27-46 的 `parallelWorkGroup`，`wg.Go` 起 goroutine）：worker 线程全部经 `LargeStackExecutor.NewWorkerThread` 创建，**禁止**用 `Task.Run`/线程池承载编译任务（§1.3 受限条目）。
- 串行版（workgroup.go:48-75 `singleThreadedWorkGroup`）：队列在**调用者线程**顺序执行——因此调用者本身必须已在大栈线程上（见入口规则 1）。确定性检验（主计划 §四.3 单线程 vs 4-checker 全等）两种模式覆盖同一栈政策。

### 8.3 入口清单（必须包大栈的调用面）

| # | 入口 | 说明 | 阶段 |
|---|---|---|---|
| 1 | **Ts.Cli `Main`** | 整个 tsc 执行（execute 层）第一件事就是 `LargeStackExecutor.Run(RealMain)`——覆盖 tsconfig 解析（它走 parser！tsconfigparsing.go:291）、串行 WorkGroup、诊断打印等一切主线程递归 | 0 |
| 2 | **WorkGroup 并行 worker** | fileloader 的 parse 任务、binder 并行、printer/emit 并行 | 3+ |
| 3 | **checker 池执行线程** | checkerpool.go:36-52（默认 4 checker）+ forEachCheckerGroupDo（checkerpool.go:148-152，内部 `core.NewWorkGroup`）——check 递归是全项目最深 | 5 |
| 4 | **LSP 请求分发线程** | 凡可能触及 parser/checker 的 handler（hover/completion/diagnostics/项目加载）；jsonrpc IO 读写线程本身不需要大栈 | 8 |
| 5 | **api 服务器请求线程** | 同上 | 9 |
| 6 | **测试执行** | `Ts.TestUtil` 提供 `RunOnLargeStack(Action)` 包装器；conformance 跑批器、fourslash、所有直接调 parser/binder/checker 的 xUnit 测试**必须**经它——xUnit worker 是普通线程池级别栈 | 3+ |

配套纪律：**递归路径内禁止 `async/await`**——await 恢复点会跳回线程池线程（1MB 栈），大栈保证随之蒸发。编译器核心（Ts.Compiler 全部 + Ts.LanguageService 的计算层）保持同步代码，异步只存在于 IO 边界（LSP stdio 读写、fswatch 事件泵），边界向计算层移交时切换到大栈线程。

### 8.4 为什么不能只调大主线程

1. **Unix 上做不到**：主线程栈由进程启动时的 `ulimit -s`/内核决定，二进制自身无法声明（Windows PE 的 `/STACK` 保留值不可移植，且 MSBuild `StackReserveSize` 仅影响 Windows 产物主线程）。
2. **工作根本不在主线程上**：parse/bind/check 全在 WorkGroup worker 与 checker 池线程；LSP 每请求一线程。主线程只是调度者。
3. **线程池不可配栈**：`ThreadPool`/`Task.Run` 的线程栈大小进程级固定，改它影响所有线程、浪费地址空间，且 .NET 无公开 per-work-item 配置。
4. **测试进程同病**：xUnit 自管线程，改宿主主线程无济于事。

因此唯一健壮方案是「**计算入口显式换线程**」——这恰好也与 tsgo 的结构同构：goroutine 边界即我们的 LargeStackExecutor 边界。

64MB 依据：主计划 §一.5 钦定；量级校验——Go 侧曾用 `TS_GO_DEBUG_STACK_LIMIT` 收紧栈做递归审计（core.go:24），tsc.js 生产 `--stack-size` 同量级。C# 帧一般比 Go 帧宽（无栈上逃逸分析、debug 帧更大），64MB 留出 ~一个数量级余量。是否提供同款环境变量覆盖见 §12 待澄清第 4 条。

---

## 9. 泛型值类型实例化与二进制体积监控

### 9.1 机制

NativeAOT 对**引用类型**类型实参保留 `__Canon` 共享（`Dictionary<string, Node>` 与 `Dictionary<string, Symbol>` 共享方法体），但**值类型**实参逐组合独立展开：每个 `Dictionary<CacheHashKey, V>`、`OrderedMap<TextRange, T>`、`List<SomeRecordStruct>` 组合都生成完整的方法体拷贝。本项目高危点：

- 结构键缓存族（主计划 §一.6；[03-checker-design.md] 全部结构键的 C# 签名）：每个 `readonly record struct` 键类型 × 每种容器 = 一份代码。checker 的十几种关系/实例化缓存键会产生几十个 Dictionary 实例化——**这是设计允许的开销**，但要计量。
- `OrderedMap<K,V>` / `LinkStore<K,V>` / `Set<T>` / `core.Stack<T>` 移植容器的实例化面。
- 纪律（写入 [05-conventions.md]）：① 不为微小差异铸造新键 struct——能复用既有键类型就复用；② 容器 helper 写成非泛型或以引用类型收口的形态优先；③ 禁止「泛型套泛型」的装饰性抽象（tsgo 没有的抽象一律不发明）。

### 9.2 体积基线与警戒线

| 时点 | 动作 |
|---|---|
| 阶段 0（hello-CLI） | 记录三平台 `dotnet publish /p:PublishAot=true` 产物尺寸为**基线 0**（典型 2-4MB 量级），写入 [11-progress.md] |
| 每阶段门禁 | 重测三平台尺寸 + 与上一门禁对比；**无解释增长 >15%** 触发调查（用 §9.3 工具定位）；参照物：同平台 tsgo 发布二进制的体积量级（含 108 libs 内嵌，数十 MB 级），最终产物预期同量级 |
| 阶段 10 | 尺寸专项：mstat 全量审计一次，清理意外实例化/未裁剪资源 |

### 9.3 工具

```bash
# 生成体积分析数据（发布时附加）
dotnet publish src/Ts.Cli -c Release -r osx-arm64 \
  /p:PublishAot=true /p:IlcGenerateMstatFile=true /p:IlcGenerateDgmlFile=true
```

- **sizoscope**（.NET 官方社区工具）：打开 `.mstat` 文件，按命名空间/类型/泛型实例化钻取体积占比——定位「哪个 record struct 键炸出了 400 份 Dictionary」的首选。
- **SizeBench**（微软，仅 Windows PE）：win-x64 产物的节级/符号级分析，作为 Windows 侧补充。
- `.dgml` 依赖图：回答「这个类型为什么被保留」（谁的静态可达性拖进来的）。
- CI 在阶段门禁 job 上把 `.mstat` 存为 build artifact，供历史对比。

---

## 10. CI analyzer 门禁与本地验证

### 10.1 编译期门禁（每次 PR）

三平台矩阵（osx-arm64 / linux-x64 / win-x64）：

1. `dotnet build -c Release`——因 §3 的 `TreatWarningsAsErrors` + 三 analyzer + globalization CA=error + BannedApi，**任何 IL2xxx/IL30xx/CA13xx/RS0030（BannedApi）直接构建失败**。关键规则号速查：
   - Trim：IL2026、IL2055、IL2057、IL2067、IL2070、IL2072、IL2075
   - AOT：IL3050、IL3051、IL3052、IL3053、IL3056
   - 单文件：IL3000、IL3001、IL3002
   - Globalization：CA1304、CA1305、CA1307、CA1309、CA1310、CA1311
   - BannedApi：RS0030
2. `dotnet test -c Release`——JIT 双轨的日常测试（快）。
3. **AOT 冒烟**（每 PR，非仅门禁）：`dotnet publish src/Ts.Cli -c Release -r <rid> /p:PublishAot=true` 必须零警告成功 + 产物跑最小冒烟（下节脚本）。ILC 的部分诊断只在 publish 时出现（跨程序集可达性分析），build 过不代表 publish 过。

### 10.2 本地验证命令（工程师日常）

```bash
# 1) 全量构建 + 测试（JIT，日常循环）
dotnet build -c Release
dotnet test -c Release

# 2) AOT 发布冒烟（本机 Apple Silicon）
dotnet publish src/Ts.Cli -c Release -r osx-arm64 /p:PublishAot=true
BIN=artifacts/publish/Ts.Cli/release_osx-arm64/tsgo   # 产物名与路径以脚手架实际为准

# 3) 冒烟脚本内容（阶段 0 起入库为 eng/smoke-aot.sh）
$BIN --version                          # 期望输出含 7.1.0-dev（core/version.go:8 对齐）
printf 'const a: number = "x";\n' > /tmp/smoke.ts
$BIN /tmp/smoke.ts ; test $? -eq 2      # 期望 TS2322 + 退出码语义与 tsgo 一致
echo 'const 𝒳 = 1;' > /tmp/smoke2.ts   # 非 BMP 冒烟：UTF-16 列号 + invariant 行为
$BIN /tmp/smoke2.ts

# 4) 阶段门禁附加：对 AOT 产物复跑一致性套件（主计划 §四.2）
dotnet test tests/Ts.Conformance.Tests -c Release -e TS_CS_EXTERNAL_BIN=$BIN
#（conformance 跑批器支持「外部二进制」模式：驱动已发布产物而非进程内调用，设计见 08-testing-strategy.md）

# 5) 体积记录
ls -l $BIN && shasum -a 256 $BIN        # 尺寸入 11-progress.md
```

### 10.3 阶段门禁专属

- 阶段 0 出口：三平台 publish 过 + `Ast/*.g.cs` 过 AOT+Trim analyzer + 诊断表计数 = 2,154（主计划 §二表）。
- 阶段 7 出口（AOT 门禁）：发布二进制编译真实大项目，输出与 tsgo 全等、性能 ≤2× tsgo。
- 阶段 10：全一致性 + fourslash 在三平台 **AOT 二进制**上 CI 全绿。

---

## 11. 已知 .NET AOT 限制对本项目的影响评估

| # | 限制 | 对本项目的影响 | 处置 |
|---|---|---|---|
| 1 | **无 JIT：泛型共享受限** | 值类型泛型逐组合展开（§9）；无分层编译/动态 PGO，稳态性能在编译期定死 | §9 监控 + 阶段 10 可评估 `OptimizationPreference=Speed`、PGO 静态化选项；性能门禁（≤2× tsgo）在阶段 7 兜底 |
| 2 | **静态构造顺序与惰性 cctor** | Go `init()`/包级 var 在程序启动时按依赖序**确定性**执行；C# cctor 是 first-touch 惰性触发。风险两条：① 移植的包级 var 若存在跨「包」初始化顺序依赖，first-touch 次序可能与 Go 不同（结果不同或 `TypeInitializationException`）；② 多线程 first-touch 同一批互相引用的 cctor 有死锁理论风险（checker 池 4 线程同时首触） | 规则（入 [05-conventions.md]）：静态字段初始化器只允许依赖**本类型内**成员与纯函数；跨类型顺序依赖显式改为 `Lazy<T>` 或 `[ModuleInitializer]`（后者需注释标注对应的 Go init() 证据）；移植 Go init() 函数时逐个登记到模块清单危险点列。附带收益：NativeAOT 会在编译期预执行可预执行的 cctor（ILC preinit），静态表（如生成的 Unicode 表用 RVA Span 而非数组）能免运行时成本 |
| 3 | **NativeLibrary / P/Invoke——fswatch 阶段用** | Go 侧 fswatch 为免 cgo 的手工 FFI：macOS 直调 CoreFoundation/CoreServices FSEvents（fswatch/fsevents_darwin_ffi.go:18-93 + 手写汇编 trampoline）、Linux fanotify/inotify（fanotify_linux.go、inotify_linux.go）、kqueue.go、windows.go。主计划阶段 8 钦定 **FileSystemWatcher 重写**（fswatch 不直译）——.NET FileSystemWatcher 在 macOS 上本就走 FSEvents、Linux 走 inotify，把 Go 的整个 FFI 层吸收掉 | 首选零 P/Invoke。**若** FileSystemWatcher 语义缺口（如 recursive watch 的 rename 事件序、fanotify 的挂载级监控）实测无法满足 project 层需求，回退路径为 `[LibraryImport]`（源生成 marshalling，AOT 安全）+ `NativeLibrary.Load` 固定路径加载 framework——两者都在允许清单（§2）。**禁止** cgo 式运行时代码生成思路与非泛型 `Marshal.GetDelegateForFunctionPointer`；回调用 `[UnmanagedCallersOnly]` 静态方法。评估结论：AOT 不构成 fswatch 阻塞项，风险在语义差异而非技术可行性（记 [12-risks.md]） |
| 4 | **反射元数据被裁剪** | 对本项目零影响面——§1 已禁产品级反射；唯一残留消费者是枚举 ToString（§6，AOT 保留）与异常栈帧名 | `StackTraceSupport=true`（§3.5）保栈帧名；不引入任何 `DynamicallyAccessedMembers` 标注需求（出现该标注 = 设计违规信号） |
| 5 | **单文件形态** | `Assembly.Location` 为空、无卫星程序集、资源必须内嵌 | §1.2 禁 Location；§5 内嵌政策本就以此为前提；locale 不走 .resx/卫星程序集正是为此（Go 同构：json.gz 内嵌） |
| 6 | **StackOverflow 不可恢复** | JIT 下同样成立，但 AOT 无法靠调试器兜底 | §8 栈政策 + 阶段 3 起全语料零崩溃门禁本身就是深递归回归网 |
| 7 | **诊断/调试体验** | AOT 产物用 lldb/WinDbg 级调试，无 EnC/热重载 | 双轨开发流程（主计划 §四.2）：日常 JIT 开发调试，AOT 只在冒烟与门禁跑；AOT-only 分歧（理论上仅剩时序/栈尺寸）由门禁复跑一致性套件捕获 |
| 8 | **启动与内存** | 利好：无 JIT 预热，CLI 冷启动快于等价 JIT 部署——与 tsgo（原生二进制）对位公平 | 阶段 10 BenchmarkDotNet 对照（不设门禁，主计划 [08-testing-strategy.md]） |

---

## 12. 待澄清

以下事项不改变主计划设计，列出供计划评审裁决：

1. **「tsconfig 解析」作为 STJ 面的措辞**：源码证据表明 tsconfig 读取走 TS parser（tsconfigparsing.go:291、704、957 的 `parser.ParseSourceFile` JSONC 路径），不经过任何 JSON 序列化器。本文档将该面重定义为「--showConfig/JSON 写出 + OrderedMap 编解码」（§4.2-1）。若主计划原意不同（例如为 tsconfig 引入独立 JSON 读取路径），需要澄清——但那将偏离 1:1 直译原则，本文档不予采纳。
2. **locale 协商 matcher**：Go 依赖 `golang.org/x/text/language.NewMatcher`（loc_generated.go:15-30、locale/locale.go:24-28 的 `language.Parse`）。InvariantGlobalization 下无 BCL 等价物，需手写微型 BCP-47 matcher（固定 14 tag 表）。主计划阶段 9 仅写「locale」一词，此工作项（约 1-2 天）请在 [09-phases.md] 阶段 9 中显式列入。
3. **测试项目是否强制 InvariantGlobalization**：本文档裁定「是」（§3.1 理由：JIT 双轨与 AOT 行为一致），主计划未明示。若评审认为测试需要 culture 场景（预期没有——tsgo 亦无），需回改 §3.2。
4. **64MB 栈的环境变量覆盖**：tsgo 有 `TS_GO_DEBUG_STACK_LIMIT`（core/core.go:24-34，收紧方向的调试开关）。建议 C# 侧对称提供 `TS_CS_DEBUG_STACK_LIMIT`（仅调试用途，默认 64MB 不变）；变量名与是否入正式文档待定。
5. **Ts.Cli 的 GC 模式初值**：主计划阶段 10 才做「ServerGC 调优」，阶段 0 脚手架需要一个初值。本文档建议初值保持默认（Workstation GC），把 `ServerGarbageCollection` 留作阶段 10 基准驱动的决策，避免过早锁定；若希望从第 0 天就模拟最终形态，请评审改判。
6. **`RegexOptions.Compiled` 的机器强制**：BannedApi 无法禁枚举值，目前靠 SYSLIB1045（建议改 GeneratedRegex 的提示）+ 评审清单。若需要硬门禁，可写一个 ~50 行的自定义 analyzer，列为可选工作项。
