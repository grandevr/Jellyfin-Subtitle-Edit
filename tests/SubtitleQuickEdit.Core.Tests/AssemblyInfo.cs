using System.Runtime.Versioning;

// The writer tests use Unix file modes, hard links and libc; they run on Linux only.
[assembly: SupportedOSPlatform("linux")]
