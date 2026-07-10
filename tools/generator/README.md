# tools/generator

代码生成器工作区。布局、npm scripts、各生成族的规格与上游同步 SOP 的**唯一权威**是
[`plan/04-codegen.md`](../../plan/04-codegen.md)；本 README 不重复其内容。

- 运行方式：`npm run gen:<family>`（各脚本以 `import.meta.dirname` 定位仓库根，不依赖 cwd）。
- 上游资产入仓副本（`extraDiagnosticMessages.json` 等）：禁止本地编辑，唯一修改途径是
  plan/04 §10 的上游同步流程；sha256 钉扎在仓库根 `UPSTREAM.lock`。
- `expected-counts.json`：各族产物计数断言；上游升级时先改此文件再重新生成。

## 已知偏差（相对 plan/04 §1.1 的平铺布局）

- `ast.json`、`ast.schema.json`、`schema.ts` 三个逐字节副本放在 `_scripts/` 子目录而非平铺：
  `schema.ts` 尾部的单例以 `resolve(自身目录, "..")/_scripts/ast.json` 定位输入
  （上游 `_scripts/schema.ts:1263-1266`），平铺放置会使逐字节副本读不到 ast.json——
  唯一不给 schema.ts 打补丁（打补丁即破坏 UPSTREAM.lock 的 sha256 钉扎与"上游同步 = 纯拷贝"）
  的解法就是复刻上游的 `_scripts/` 目录几何。上游同步命令因此保持纯拷贝：
  `cp tsgo/_scripts/{ast.json,ast.schema.json,schema.ts} tools/generator/_scripts/`。
- `ast.schema.json` 尚未钉入 `UPSTREAM.lock`（锁内现有 ast.json 与 schema.ts 两条）；
  它仅供编辑器校验 ast.json，不参与生成。补钉与否由升级原子 PR 决定。
