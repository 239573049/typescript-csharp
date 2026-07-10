# tools/generator

代码生成器工作区。布局、npm scripts、各生成族的规格与上游同步 SOP 的**唯一权威**是
[`plan/04-codegen.md`](../../plan/04-codegen.md)；本 README 不重复其内容。

- 运行方式：`npm run gen:<family>`（各脚本以 `import.meta.dirname` 定位仓库根，不依赖 cwd）。
- 上游资产入仓副本（`extraDiagnosticMessages.json` 等）：禁止本地编辑，唯一修改途径是
  plan/04 §10 的上游同步流程；sha256 钉扎在仓库根 `UPSTREAM.lock`。
- `expected-counts.json`：各族产物计数断言；上游升级时先改此文件再重新生成。
