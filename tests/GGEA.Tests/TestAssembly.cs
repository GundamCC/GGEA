using Xunit;

// 多个识图流程测试使用短时真实等待；并行处理大图会争用 CPU，让计时断言在 CI 和正式发布中偶发超时。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
