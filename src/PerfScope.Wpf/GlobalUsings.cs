// WPF 的 XAML 编译会用临时工程（_wpftmp）再过一遍源码，而临时工程不继承 Directory.Build.props
// 里的 ImplicitUsings，导致「正常构建能过、XAML 编译报找不到 Path」这类诡异错误。
// 这里显式声明全局 using，两种构建路径都能拿到同一套命名空间。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
