# External Module

`Lib/Modules/External.cs`

Loads a user-supplied .NET assembly from disk via reflection, locates a named type that implements `IExternalStep`, instantiates it, and delegates `Execute`. Allows teams to inject arbitrary C# logic into any job pipeline without modifying the framework.

## IExternalStep Interface

`Lib/Modules/IExternalStep.cs`

Interface that external assemblies must implement to be callable via the External module. Has the same `Execute(Dictionary<string, object> sharedState)` signature as `IModule`.

## Assembly Loading

Uses `Assembly.LoadFrom(assemblyPath)` to load the DLL, then locates the specified type by name. The assembly path in the job config supports `{TOKEN}` expansion via `PathHelper`.

## Config Properties

| JSON Property | Required | Description |
|---|---|---|
| `type` | Yes | `"External"` |
| `assemblyPath` | Yes | Path to the .NET assembly DLL (supports `{TOKEN}` expansion) |
| `typeName` | Yes | Fully qualified type name implementing `IExternalStep` |

## Example

```json
{
  "type": "External",
  "assemblyPath": "{ETL_ROOT}/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
  "typeName": "ExternalModules.CardCustomerSpendingProcessor"
}
```
