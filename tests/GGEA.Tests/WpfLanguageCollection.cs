namespace GGEA.Tests;

/// <summary>WPF 与语言属于进程级启动状态，验证时独占进程，避免其他测试读到临时日文资源。</summary>
[CollectionDefinition("WpfLanguage", DisableParallelization = true)]
public sealed class WpfLanguageCollection;
