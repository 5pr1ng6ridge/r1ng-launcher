using Xunit;

// WPF BAML loading is not safe to run on several threads at once: `Application.LoadComponent`
// uses a shared XAML schema-context cache and concurrent real-window construction can randomly throw
// "The given key 'ScrollUnit' was not present in the dictionary" (WpfXamlType.FindMember). The suite
// is small, so run tests sequentially and keep the WPF/STA window tests deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
