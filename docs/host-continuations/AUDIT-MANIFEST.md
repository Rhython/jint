# 审计清单

## 1. 基线身份

```text
Artifact: jint-4.13.0.zip
SHA-256: 99e3dbd3e98c01ce496e82673da4c1c75e748801de8e0d635fae9d43598f63ab
ZIP comment/revision: c244f9281795738e68df321ceb584c57935c9c66
Archive timestamp: 2026-07-15
```

注意：源码内部 `VersionPrefix` 为开发线版本，不作为上传归档身份；审计身份以输入归档哈希和 ZIP comment 为准。

## 2. 工具链身份

```text
.NET SDK: 10.0.100
SDK commit: b0f34d51fc
MSBuild: 18.0.2+b0f34d51f
Host runtime: 10.0.0
RID: linux-x64
Offline servicing runtime/AOT packs: 10.0.9
```

## 3. 最终交付物

生成脚本会在交付目录产生：

- `jint-host-continuations-source.zip`：清洁源码、测试、示例、文档和复现脚本；
- `jint-host-continuations.patch`：baseline 到最终源码的完整 Git patch；
- `jint-host-continuations.bundle`：含 baseline 和 change 两个 commit 的 Git bundle；
- `jint-host-continuations-audit-evidence.zip`：构建日志、TRX、NativeAOT 文件信息和运行输出；
- `jint-host-continuations-aot-linux-x64.tar.gz`：已验证 native smoke binary及其 debug sidecar；
- `CHANGESET.csv`：逐文件状态、行数与 SHA-256；
- `SHA256SUMS.txt`：所有交付物哈希。

`docs/host-continuations/CHANGESET.csv` 有意不列出自身，避免形成无法稳定计算的递归自哈希；完整 Git diff 仍包含该 CSV 文件。

最终 `.gitattributes` 还把上传基线中原本为 CRLF 的 5 个既有源码文件标记为 `-text`。这些文件内容没有功能性修改；该规则只用于阻止 Git 在 source ZIP / bundle 中把它们静默规范化为 LF，确保“上传归档 → 应用补丁 → 最终源码”的字节级一致性。

## 4. 排除项

源码 ZIP 和 Git change commit 排除：

- `.git/`
- `artifacts/`
- `bin/`
- `obj/`
- `TestResults/`
- 含本机绝对路径的 `NuGet.offline.config`
- 临时诊断程序和中间 trace 文件

证据 ZIP 只收录最终验证日志和最终 TRX，不把早期失败/诊断噪声作为通过证据；完整工作目录仍保留早期日志供必要时追查。

## 5. 完整性验证

```bash
sha256sum -c SHA256SUMS.txt
git bundle verify jint-host-continuations.bundle
unzip -t jint-host-continuations-source.zip
unzip -t jint-host-continuations-audit-evidence.zip
tar -tzf jint-host-continuations-aot-linux-x64.tar.gz
```

补丁应用：

```bash
unzip jint-4.13.0.zip
cd jint-4.13.0
git apply --check ../jint-host-continuations.patch
git apply ../jint-host-continuations.patch
```
