[![](https://img.shields.io/nuget/v/soenneker.clamav.freshclam.util.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.clamav.freshclam.util/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.clamav.freshclam.util/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.clamav.freshclam.util/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.clamav.freshclam.util.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.clamav.freshclam.util/)

# Soenneker.Clamav.Freshclam.Util

A cross-platform .NET API for updating ClamAV virus definitions with bundled official FreshClam runtimes.

## Installation

```bash
dotnet add package Soenneker.Clamav.Freshclam.Util
```

```csharp
services.AddFreshclamUtilAsSingleton();

IFreshclamUtil freshclam = provider.GetRequiredService<IFreshclamUtil>();
await freshclam.Update("data/clamav", cancellationToken: cancellationToken);
```

FreshClam incrementally updates an existing database whenever upstream diffs are available and performs a complete download when no usable seed exists. The default database directory is `Resources/clamav-database` beneath the application output directory.

The managed library is MIT-licensed. Its Linux and Windows native package dependencies preserve the GPL-2.0-only ClamAV runtime licensing and upstream notices.
