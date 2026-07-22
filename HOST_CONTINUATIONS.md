# Implicit Host Continuations

本源码树包含用于同步外观 JavaScript 调用异步 C# 宿主操作的实验性 Jint 扩展。

完整设计、API、限制、逐文件变更和验证证据索引见：

- [`docs/host-continuations/README.md`](docs/host-continuations/README.md)

离线复现入口：

```bash
DOTNET_ROOT=/path/to/dotnet-sdk \
NUGET_PACKAGES=/path/to/offline/packages \
./eng/host-continuations/offline-build.sh
```
