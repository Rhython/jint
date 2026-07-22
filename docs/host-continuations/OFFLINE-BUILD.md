# 离线构建与复现

## 1. 本次输入

| 文件 | 大小 | SHA-256 |
|---|---:|---|
| `jint-4.13.0.zip` | 3,849,000 | `99e3dbd3e98c01ce496e82673da4c1c75e748801de8e0d635fae9d43598f63ab` |
| `dotnet-sdk-10.0.100-linux-x64.tar.gz` | 239,125,653 | `a9631cc6bfad0ef167383ac654b54254bad95a6fb4b6f4309fa78f558055e637` |
| `nuget-packages.zip(1).001` | 524,288,000 | `d0480cc69e194084515282fc8a758a97dcd58843c95769baea9e5843166e74eb` |
| `nuget-packages.zip(1).002` | 404,151,869 | `ae0c06e693a25cb9617b3c5548618d619179c4265a0cd1c3f49f8db63de2c178` |
| 拼接后的 NuGet ZIP 流 | 928,439,869 | `e47c0dee0b661a856795d0e7c3d34351dc13be71c2e202140b97b6c41abec895` |

Jint ZIP comment：`c244f9281795738e68df321ceb584c57935c9c66`。

## 2. 解压

```bash
mkdir -p /opt/dotnet-10.0.100
mkdir -p /opt/nuget-offline

tar -xzf dotnet-sdk-10.0.100-linux-x64.tar.gz \
  -C /opt/dotnet-10.0.100

cat 'nuget-packages.zip(1).001' 'nuget-packages.zip(1).002' \
  > /tmp/nuget-packages.zip
unzip -q /tmp/nuget-packages.zip -d /opt/nuget-offline
```

本次归档解压后的 global packages folder 是：

```text
/opt/nuget-offline/packages
```

## 3. 一键复现

```bash
cd jint-host-continuations

DOTNET_ROOT=/opt/dotnet-10.0.100 \
NUGET_PACKAGES=/opt/nuget-offline/packages \
RID=linux-x64 \
./eng/host-continuations/offline-build.sh
```

脚本执行：

1. 生成不含网络 package source 的临时 NuGet config；
2. 离线 restore；
3. Release net10.0 build；
4. 23 个 continuation 专项测试；
5. 3648 项完整回归；
6. NativeAOT restore/publish；
7. 运行 native smoke binary并检查成功标记。

## 4. 工具链差异及 opt-in 兼容层

### 4.1 Roslyn 版本

源码包 pin：

```text
Microsoft.CodeAnalysis.CSharp 5.6.0
```

上传 SDK 10.0.100 自带 analyzer host/编译器：

```text
Roslyn 5.0.0
```

5.6 analyzer 无法由 5.0 host 加载。因此在明确传入：

```text
-p:OfflineUseSdkRoslyn=true
```

时：

- `Jint.SourceGenerators` 改为 `net10.0`；
- 移除 package Roslyn references；
- 引用 SDK `Roslyn/bincore` assemblies。

默认构建不设置该属性，仍保持上游 netstandard2.0/5.6 配置。

### 4.2 Runtime/AOT servicing pack

SDK metadata 请求 10.0.0 pack，但离线缓存提供 10.0.9：

- `Microsoft.NETCore.App.Runtime.linux-x64 10.0.9`
- `Microsoft.DotNet.ILCompiler 10.0.9`
- `Microsoft.NET.ILLink.Tasks 10.0.9`

AOT 命令额外传入 `-p:OfflineUseServicingPacks=true`，只对 RID/NativeAOT restore 显式选择缓存内 servicing pack；普通测试仍运行 SDK 自带的 10.0.0 shared runtime。使用其他 SDK/cache 组合时，应移除或更新这些值，而不是无条件沿用。

### 4.3 `NU1603`

离线 cache 用 10.0.9 满足 SDK 的 10.0.0 ILLink 请求时产生 `NU1603` approximate-best-match 提示；项目全局 warnings-as-errors，因此 opt-in `Directory.Build.targets` 在保留各项目既有 `NoWarn` 的基础上追加 `NU1603`。不使用命令行 `-p:NoWarn=...`，因为那会覆盖 Jint 项目原有的 AOT 分析警告配置。

### 4.4 XML documentation

上传 SDK 的 Roslyn 5.0 在该源码生成器组合下生成 XML API docs 时触发内部 `CS1569`。离线审计构建传入：

```text
-p:GenerateDocumentationFile=false
```

这不改变程序集执行代码。正常受支持工具链应重新启用 XML docs。

### 4.5 ZIP executable bit

NuGet ZIP 解压不保留 `ilc` executable bit。复现脚本只对 cache 内名为 `ilc` 的工具执行 `chmod +x`，然后发布 NativeAOT。

### 4.6 时区别名

若系统没有 `Europe/Kiev`，脚本尝试：

1. 使用显式 `TZDIR`；
2. 使用 Python `tzdata`；
3. 复制系统 tzdata 到临时目录并建立 `Europe/Kiev -> Kyiv` 链接。

## 5. 手工核心命令

```bash
export DOTNET_ROOT=/opt/dotnet-10.0.100
export PATH="$DOTNET_ROOT:$PATH"
export NUGET_PACKAGES=/opt/nuget-offline/packages

COMMON=(
  -p:TargetFrameworks=net10.0
  -p:OfflineUseSdkRoslyn=true
  -p:GenerateDocumentationFile=false
  -p:UseSharedCompilation=false
)
AOT=("${COMMON[@]}" -p:OfflineUseServicingPacks=true)

dotnet restore Jint.Tests/Jint.Tests.csproj \
  --configfile /tmp/offline-nuget.config \
  "${COMMON[@]}"

dotnet build Jint.Tests/Jint.Tests.csproj \
  -c Release -f net10.0 --no-restore \
  "${COMMON[@]}"

dotnet test Jint.Tests/Jint.Tests.csproj \
  -c Release -f net10.0 --no-build --no-restore \
  --filter 'FullyQualifiedName~HostContinuationTests' \
  "${COMMON[@]}"

dotnet test Jint.Tests/Jint.Tests.csproj \
  -c Release -f net10.0 --no-build --no-restore \
  "${COMMON[@]}"

dotnet restore Jint.HostContinuations.AotSmoke/Jint.HostContinuations.AotSmoke.csproj \
  -r linux-x64 --configfile /tmp/offline-nuget.config \
  "${AOT[@]}"

dotnet publish Jint.HostContinuations.AotSmoke/Jint.HostContinuations.AotSmoke.csproj \
  -c Release -f net10.0 -r linux-x64 --no-restore \
  "${AOT[@]}"
```
