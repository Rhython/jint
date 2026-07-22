# 安全、可靠性与运维风险

## 1. 线程安全边界

最高风险是后台完成线程触碰 Engine、`JsValue` 或 `user` 数据。审计要求：

- request snapshot 在 handler 返回前完成；
- operation 闭包只包含 CLR DTO、token 和 thread-safe service；
- result converter 只由 owner resume callback 调用；
- 不把 Engine/JsValue 存入 Task state、channel message 或 completion DTO；
- 开发和测试环境为所有 user façade 添加 owner-thread assert。

## 2. 脚本信任模型

本变更没有扩大 Jint sandbox 保证。对于不可信脚本：

- 只暴露最小 façade；
- 使用 Jint statement/time/memory/recursion constraints；
- 禁止任意 CLR type/reflection；
- AST 校验 host effect 调用位置；
- 对字符串、资源大小、请求次数、挂起时长做宿主配额；
- 不允许脚本控制 scheduler、continuation token 或内部 Engine 对象。

## 3. 挂起资源泄漏

长时间未完成 operation 会保留：

- script/function environments；
- closures 和可达 JS object graph；
- suspend-data 中的 iterator/reference/buffer；
- host operation state。

必须设置：

- per-operation timeout；
- workflow 总生命周期；
- user/session 断开时 cancellation；
- Engine Dispose 策略；
- pending run 数量和保留内存监控。

## 4. 重复与迟到完成

`HostOperation` 只消费第一个完成。服务层仍应使用 request id 和幂等规则处理：

- 用户重复提交；
- timeout 后迟到响应；
- 重试同时返回；
- session 被替换；
- shutdown/Dispose 后 callback。

## 5. 调度器失败

- inline callback：运行时拒绝；
- 错线程 callback：completion fault，Engine 隔离；
- `Post` 抛异常：run fault；
- event loop 永不消费：Task 永不完成，需宿主 watchdog/cancel；
- event loop shutdown：应先 cancel/Dispose 所有 Engine。

## 6. 语义边界漂移

Jint 上游会继续修改 async/generator suspend-data、environment pooling 和 call fast paths。升级时必须重新审计：

- `ISuspendable`；
- `ExecutionContext`；
- `JintStatementList`；
- 所有 `SuspendData` 类型；
- `ScriptFunction.Call` 和 pooling；
- `JintCallExpression`/`JintNewExpression`；
- `try/finally`；
- StackGuard；
- event loop/microtask。

不能把补丁机械套到新版本后只以“能编译”作为通过标准。

## 7. 错误信息

运行时主动拒绝限制边界时，错误字符串是审计和测试的一部分，但不是稳定协议。产品逻辑不应按 message 分支；建议后续增加专用异常/错误码类型，再由宿主统一映射。

## 8. 许可证

新增代码放入 Jint 源码树，应沿用项目许可证和版权策略。最终 patch/bundle 保留基线所有许可证文件。引入生产仓库前由项目法律/合规流程确认补丁著作权归属和上游贡献策略。
