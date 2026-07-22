# NativeAOT 审计

## 1. 已证明的范围

本次实际执行了：

- .NET SDK 10.0.100；
- Linux x64 RID；
- runtime/AOT/ILLink servicing packs 10.0.9；
- Release NativeAOT publish；
- 运行生成的 ELF；
- 三段未改写同步脚本宿主调用；
- CLR operation 非阻塞完成；
- 所有 JS snapshot/result/final converter 和 resume callback 的 owner-thread 断言。

Native 文件信息：

```text
ELF 64-bit LSB pie executable, x86-64
interpreter /lib64/ld-linux-x86-64.so.2
stripped
size: 12,184,024 bytes
```

执行输出：

```text
HOST_CONTINUATION_AOT_OK:sent:payload
```

## 2. AOT smoke 的保留策略

项目包含：

```xml
<TrimmerRootAssembly Include="Jint" />
```

因此该 smoke 证明：

- Jint 和新增 continuation 代码能够被 NativeAOT 编译；
- 测试路径在 native binary 中实际执行成功；

但它**不证明** Jint 已可安全裁剪为最小程序集。rooting 整个 Jint 是为了把“代码生成/运行正确性”与“Jint 全部反射互操作的裁剪注解修复”分开审计。

## 3. AOT/trim 警告

最终 publish：

```text
105 warning lines
0 errors
```

代码分布：

| 代码 | 次数 |
|---|---:|
| IL2026 | 4 |
| IL2055 | 2 |
| IL2060 | 3 |
| IL2062 | 4 |
| IL2067 | 19 |
| IL2069 | 1 |
| IL2070 | 11 |
| IL2072 | 35 |
| IL2075 | 6 |
| IL2077 | 2 |
| IL2080 | 4 |
| IL2098 | 1 |
| IL2111 | 1 |
| IL3050 | 12 |

最终日志没有把警告定位到新增的 `Engine.HostContinuations.cs` 或 `Runtime/Continuations/*`。主要警告来自 Jint 既有的：

- reflection accessor/type descriptor/object wrapper；
- runtime `Type`/constructor/method/property 枚举；
- expression tree delegate 构造；
- `MakeGenericMethod`；
- dynamic object binder；
- System.Text.Json 非 source-generated deserialize；
- Task/ValueTask reflection conversion；
- operator/extension method discovery。

## 4. 生产 NativeAOT 规则

为了把已验证路径与高风险路径隔离：

- 使用 `CreateHostContinuationFunction<TRequest,TResult>`；
- request/result 使用显式 DTO；
- 在 owner 线程手写 `JsValue` 转换；
- 不返回 `JsValue` 穿过 async 边界；
- 不开放任意 `AllowClr()`；
- 避免把任意 POCO 交给 `JsValue.FromObject`；
- JSON 使用 source-generated `JsonSerializerContext`；
- 对每个 host façade 建立 NativeAOT smoke；
- 每个目标 RID 独立 publish/run；
- 逐步移除 `TrimmerRootAssembly`，并在业务限定表面上把 IL warnings 提升为 errors。

## 5. 参数复制问题

早期 `[.. arguments]` 在本次混合 servicing 工具链生成的 native 程序中触发 runtime fail-fast。最终使用显式循环复制，NativeAOT 成功。该修复说明不能仅以“托管 build/test 成功”判定 AOT 兼容，必须执行生成的 native binary。

## 6. 未声明的保证

目前不声明：

- warning-free trimming；
- 任意 CLR reflection interop 可用；
- Windows/macOS/ARM64 已验证；
- single-file 动态库加载场景已验证；
- ICU/Temporal/完整国际化数据已验证；
- 无 `TrimmerRootAssembly` 的最小 native image 已验证。
