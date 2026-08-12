# Third-Party Notices

ZCompare uses or references the following third-party projects.

## DocumentFormat.OpenXml

- Project: Open XML SDK
- Package: `DocumentFormat.OpenXml` 3.5.1
- Source: https://github.com/dotnet/Open-XML-SDK
- License: MIT

The package is used to read OOXML workbook packages without automating Microsoft Excel.

## OfficeCLI

- Project: OfficeCLI
- Source: https://github.com/iOfficeAI/OfficeCLI
- Source revision reviewed: `459b1a473faf33f2f52e697ac6d265a3f67b176a`
- License: Apache License 2.0
- Referenced areas: Excel number/date display formatting, theme-color resolution, and spreadsheet interaction conventions.

Only a narrow, independently integrated subset of display-formatting and theme-color behavior was used as a reference. ZCompare does not include OfficeCLI's HTML renderer, watch server, formula engine, or executable. Source-derived sections retain origin and license comments in the corresponding source files. OfficeCLI is not a build or runtime dependency.

## Test packages

- xUnit.net (`xunit`, `xunit.runner.visualstudio`) — Apache License 2.0
- Microsoft.NET.Test.Sdk — MIT
- coverlet.collector — MIT

These test packages are not included in release binaries.
