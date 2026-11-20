// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Yui-Wei

using System.Reflection;
[assembly: AssemblyTitle("拾时 · DayTrace")]
[assembly: AssemblyProduct("DayTrace")]
[assembly: AssemblyVersion(DayTrace.AppVersion.Current + ".0")]
[assembly: AssemblyFileVersion(DayTrace.AppVersion.Current + ".0")]
[assembly: AssemblyInformationalVersion(DayTrace.AppVersion.Current)]
namespace DayTrace {
 public static class AppVersion {
  public const string Current = "1.1.5";
 }
}
