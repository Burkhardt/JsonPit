# Getting Started with JsonPit 3.7.3

This guide is written for practical implementation work, especially when you want to use JsonPit from NuGet packages in another service such as OTW / AfricaStage.

It is based on the current JsonPit 3.7.3 code and tests in this repository.

## 3.7.3 key decisions

- The supported cloud-backed provider claim for the package stack is `OneDrive`, `GoogleDrive`, and `Dropbox`.
- `PitItem.Id` is now the canonical framework identifier.
- Legacy persisted payloads that still contain `Name` without `Id` are normalized internally by copying `Name` into `Id`, while preserving `Name`.
- Future use of `Name` as an application-defined custom field remains supported outside the framework identifier contract.

## Purpose and Mental Model

JsonPit is a file-based, JSON-backed storage library for `Id`-identified items that need to be shared, synchronized, and reloaded across machines or processes without introducing a database server.

The core concepts are:

- `Pit`: a named container stored on disk
- `PitItem`: one `Id`-identified JSON object inside the pit
- `PitItems`: the history of versions for one item key

JsonPit is not a relational table abstraction.

Think of it like this:

- A `Pit` is closer to a synchronized JSON document store than to a SQL table.
- A `PitItem` is a JSON object identified by `Id`, with `Modified`, `Deleted`, `Note`, and arbitrary attributes.
- JsonPit keeps history per item key, so updates are versioned rather than overwritten blindly in memory.

That makes JsonPit useful when:

- the storage root lives in a synchronized cloud-backed directory
- multiple servers or machines may read and write over time
- the schema evolves gradually
- the data volume is small enough that file-based persistence is still the right tradeoff
 
 *<i>(the meaning of 'small enough' in this context may considerably change with the IO speed of your machine's file system and the size of your machine's memory.</i>

It is not trying to replace a transactional database.

## Package Setup

Use the NuGet package ids at version `3.7.3`:

- `JsonPit`
- `RaiUtils`
- `OsLibCore`

Typical install commands:

```bash
dotnet add package JsonPit --version 3.7.3
dotnet add package RaiUtils --version 3.7.3
dotnet add package OsLibCore --version 3.7.3
```

Typical namespaces in code:

```csharp
using JsonPit;
using Newtonsoft.Json.Linq;
using OsLib;
using RaiUtils;
```

Important naming detail:

- NuGet package id: `OsLibCore`
- C# namespace: `OsLib`
- NuGet package id: `RaiUtils`
- C# namespace: `RaiUtils`

## Storage Root and OsLib Configuration

JsonPit commonly uses OsLib for path selection, but the current approach is to read one explicit configured root from `Os.Config.Cloud` rather than relying on a preferred-root helper on `Os`.

For shared synchronized storage, configure OsLib explicitly rather than hard-coding machine-specific special cases.

Current OsLib default config location:

- `~/.config/RAIkeep/osconfig.json5`

Typical cloud-root config example:

```json5
{
   Cloud: {
      GoogleDrive: "/Users/me/Library/CloudStorage/GoogleDrive/AfricaStage/"
   }
}
```

If your service is meant to work against a shared synchronized folder, prefer deriving the pit root from one explicit configured root.

Example:

```csharp
var configuredCloudRootText = (string?)Os.Config.Cloud?.GoogleDrive
   ?? throw new InvalidOperationException("Set Cloud.GoogleDrive in ~/.config/RAIkeep/osconfig.json5 first.");

var configuredCloudRoot = new RaiPath(configuredCloudRootText);
```

## Quick Start From The WWWA Integration Test

If you want the simplest path to create a pit and seed it from the sample JSON5 data used by integration tests, use the same pattern as `WWWA_IntegrationTest_CloudDrive_Idempotency`.

Core path setup:

```csharp
var testDirInCloud = configuredCloudRoot / "RAIkeep" / "WwwaTests";
var personPitFile = new PitFile(testDirInCloud, "Person");
var personPit = new Pit(personPitFile);
```

Seed the pit from the copied sample file:

```csharp
var sampleDir = configuredCloudRoot / "RAIkeep" / "sample";
var personData = new TextFile(sampleDir, "Person", "json5").ReadAllText();
personPit.AddItems(personData);
personPit.Save();
```

Important test setup detail:

- The test project has an MSBuild target named `SyncSamplesToCloud` that copies `sample/*.json5` (and other files under `sample/**`) to the cloud sample directory after build.
- See [JsonPit/JsonPit.Tests/JsonPit.Tests.csproj](JsonPit/JsonPit.Tests/JsonPit.Tests.csproj) for the target configuration.

Reference implementation:

- [JsonPit/JsonPit.Tests/JsonPitRealWorldIntegrationTests.cs](JsonPit/JsonPit.Tests/JsonPitRealWorldIntegrationTests.cs#L22)

## Basic End-to-End Example

This is a minimal but realistic flow using the current API.

```csharp
using System;
using System.Collections.Generic;
using JsonPit;
using OsLib;
using RaiUtils;

var configuredCloudRootText = (string?)Os.Config.Cloud?.GoogleDrive
   ?? throw new InvalidOperationException("Set Cloud.GoogleDrive in ~/.config/RAIkeep/osconfig.json5 first.");
var configuredCloudRoot = new RaiPath(configuredCloudRootText);

var pitRoot = configuredCloudRoot / "AfricaStage" / "OTW" / "person";
pitRoot.mkdir();

var people = new Pit(
   pitDirectory: pitRoot.Path,
   readOnly: false,
   autoload: true,
   backup: false);

var max = new PitItem("Max");
max.SetProperty(new { Email = "max@example.org" });
max.SetProperty(new { Phone = "+27-82-000-0000" });
max.SetProperty(new { ComPref = new[] { "WhatsApp", "Email" } });

people.Add(max);
people.Save();

var reloaded = new Pit(
   pitDirectory: pitRoot.Path,
   readOnly: false,
   autoload: true,
   backup: false);

var loadedMax = reloaded["Max"];
if (loadedMax == null)
   throw new InvalidOperationException("Expected person Max to exist.");

var email = loadedMax["Email"]?.ToString();
var phone = loadedMax["Phone"]?.ToString();
var comPref = loadedMax["ComPref"]?.ToObject<List<string>>() ?? new List<string>();

Console.WriteLine($"{loadedMax.Id}: {email} / {phone}");
Console.WriteLine(string.Join(", ", comPref));
```

What this does:

- opens or creates a pit rooted at a shared path
- creates a `PitItem` named `Max`
- adds arbitrary attributes
- saves the pit
- reopens it from disk
- reads the item back by key

## Recommended Server Usage Pattern

For application servers, JsonPit is most useful when a `Pit` is treated as a long-lived in-memory object, usually owned by a singleton service or a static field that lives for the lifetime of the server process.

That is the practical mental model:

- open the pit once during startup
- keep that `Pit` instance in memory
- answer normal requests from the in-memory `Pit`
- call `Save()` when it is useful and timely to persist and synchronize

In other words, request handling is primarily in-memory, not file-first.

The main benefit of using the JsonPit API rather than manipulating JSON files directly is that the `Pit`, `PitItem`, and related classes carry the burden of:

- representing the current item state in memory
- tracking item modifications and delete markers
- maintaining item history
- writing the canonical on-disk representation correctly
- merging synchronized changes from other writers when `Save()` / reload paths run

That means most feature code should not think in terms of “how do I rewrite the underlying JSON file safely?” It should think in terms of “how do I update the in-memory pit item correctly?”

## Persistence Model: Asynchronous Persistence With Eventual Durability

The better phrase for JsonPit is:

- `asynchronous persistence with eventual durability`

This is more accurate than loosely calling it a transactional system or pretending it offers immediate cross-machine consistency.

For a single server instance:

- the server typically reads from its in-memory `Pit`
- the server typically updates the in-memory `Pit`
- the response can usually be produced from memory without waiting for cloud synchronization

For multi-server synchronization:

- another server may not see your changes immediately
- cloud propagation takes time
- calling `Save()` is the moment where the current server writes its changes and also participates in picking up synchronized changes from other servers

So the practical promise is not real-time cross-server visibility. The practical promise is that the pit becomes durable and converges through the shared synchronized storage over time.

This is why JsonPit usually does not need database-style transactional thinking for a small service feature.

The usual application pattern is simpler:

- keep the working set in memory
- save at meaningful boundaries
- tolerate synchronization delay between machines
- reload or save again when cross-server freshness matters

## Updating an Existing Item

The normal update pattern is:

1. fetch the existing item
2. add or change one or more properties
3. add the item back to the pit
4. save

Example:

```csharp
var existing = people["Max"];
if (existing == null)
   throw new InvalidOperationException("Max does not exist.");

existing.SetProperty(new { Phone = "+27-82-111-2222" });
existing.SetProperty(new { Instagram = "max.africastage" });

people.Add(existing);
people.Save();
```

Notes:

- `PitItem.SetProperty(...)` updates only the provided properties.
- If the new value is identical to the old value, JsonPit does not treat it as a change.
- `Pit.Add(...)` stores a new historical version for that item key when the item actually changed.

There is also a convenience setter:

```csharp
people.PitItem = existing;
```

But for onboarding, `people.Add(existing)` is clearer.

## Flexible Attributes and Schema Evolution

JsonPit works well when your item shape evolves over time.

Example:

```csharp
var person = people["Max"] ?? new PitItem("Max");

person.SetProperty(new { Email = "max@example.org" });
person.SetProperty(new { Phone = "+27-82-000-0000" });

people.Add(person);
people.Save();
```

Later you can add more attributes without a migration step:

```csharp
person.SetProperty(new { Instagram = "max.africastage" });
person.SetProperty(new
{
   Address = new
   {
      Street = "42 Long Street",
      City = "Cape Town",
      Country = "South Africa"
   }
});

people.Add(person);
people.Save();
```

You can also extend an item with raw JSON or `JObject` / `JArray` when needed:

```csharp
person.ExtendWith(new JObject { { "Facebook", "max.africastage.fb" } });
```

Recommended default:

- prefer `SetProperty(new { ... })` for normal typed C# usage
- use `Extend(...)` or `ExtendWith(...)` when you are already working with JSON objects or need dynamic merging behavior

## Lookup and Query Examples

JsonPit does not provide a database-style query language or indexes.

The recommended pattern is:

- get one item by id using `pit["Id"]` or `pit.Get("Id")`
- enumerate current items using `pit.AllUndeleted()`
- filter with LINQ in memory

### Get one item by id

```csharp
var max = people["Max"];
var alsoMax = people.Get("Max");
```

Practical difference:

- `people["Max"]` returns the current undeleted `PitItem` or `null`
- `people.Get("Max")` returns the current undeleted `JObject` view or `null`

For most application code, the indexer is the more convenient starting point.

### Enumerate all current items

```csharp
foreach (var item in people.AllUndeleted())
{
   Console.WriteLine(item["Id"]?.ToString());
}
```

Important nuance:

- `Pit` implements `IEnumerable<PitItems>`
- iterating `foreach (var entry in people)` gives you item histories, not just current items
- for normal application reads, prefer `AllUndeleted()`

### Filter items that have a given attribute

```csharp
var withInstagram = people
   .AllUndeleted()
   .Where(item => item["Instagram"] != null)
   .ToList();
```

### Simple attribute-based selection

```csharp
var whatsappFirst = people
   .AllUndeleted()
   .Where(item => item["ComPref"] is JArray prefs && prefs.Any(p => p?.ToString() == "WhatsApp"))
   .ToList();
```

### Enumerate keys

```csharp
foreach (var key in people.Keys)
{
   Console.WriteLine(key);
}
```

Use `Keys` when you want the known item names, and `AllUndeleted()` when you want current active item payloads.

## Historical Versions

JsonPit keeps per-item history.

For point-in-time retrieval:

```csharp
var older = people.GetAt("Max", DateTimeOffset.UtcNow.AddMinutes(-5));
```

That is useful when you explicitly care about the historical value at or before a given timestamp.

If you do not need history, ignore `PitItems` and `GetAt(...)` and focus on:

- `pit["Id"]`
- `pit.Add(item)`
- `pit.Save()`
- `pit.AllUndeleted()`

## Persistence and Storage Shape

Conceptually, a pit is persisted as a canonical `.pit` file plus supporting change files used for synchronized update flows.

The important operational picture is:

- you choose a pit root directory
- JsonPit writes the pit's canonical data there
- `Save()` persists current in-memory changes
- reopening the same pit path loads the existing data
- JsonPit can merge change files when synchronized updates arrive from other machines

In other words, JsonPit is designed to live comfortably inside shared folders such as:

- Google Drive
- Dropbox
- OneDrive
- another synchronized directory chosen explicitly by your service

For typical application usage, think in terms of:

- one stable pit root per feature
- explicit `Save()` after meaningful changes
- reopening the same root to reload persisted state

## JsonPit + OsLib + RaiUtils

These packages are often used together.

### OsLib

Use OsLib for:

- explicit configured cloud roots via `Os.Config.Cloud`
- path helpers like `Os.UserHomeDir`, `Os.AppRootDir`, `Os.TempDir`, `Os.LocalBackupDir`
- file and directory abstractions such as `RaiFile`
- buffered cloud classification through `RaiPath.Cloud`

### RaiUtils

JsonPit depends on RaiUtils internally and you may use it alongside JsonPit for general utility functions in your application layer.

### RaiPath

`RaiPath` is the most practical helper when composing stable pit roots:

```csharp
var root = configuredCloudRoot / "AfricaStage" / "OTW" / "person";
root.mkdir();

var people = new Pit(root.Path, readOnly: false, autoload: true, backup: false);
```

That is preferable to scattering raw `Path.Combine(...)` calls mixed with machine-specific cloud folder assumptions.

## Practical Guidance and Caveats

### Good fit

JsonPit is a good fit when:

- the data set is small to moderate
- human-readable JSON on disk is useful
- the schema changes over time
- you want file-based sharing through synchronized storage
- you do not want to introduce a database server for a small feature

### Keep it simple

For a small backend feature, prefer:

- one pit per concept, for example `person`
- one item key per business identity, for example `Max`
- shallow, readable attribute names
- one stable shared root rather than ad hoc per-machine locations

### Concurrency

JsonPit is designed for synchronized multi-process and multi-machine scenarios, but it is not a transactional RDBMS.

Think in terms of:

- asynchronous persistence with eventual durability
- item version history
- merge/reload cycles
- explicit save boundaries

Do not assume database-style locking or cross-item transactions.

### Synchronization delays

If the pit lives in cloud-backed storage, changes may not appear instantly on another machine.

Design accordingly:

- save explicitly
- allow sync latency
- avoid immediate cross-server assumptions after a write unless you have your own retry or observer logic

If you keep one long-lived `Pit` instance per feature in your server process, this becomes much easier to reason about:

- local request handling stays simple and memory-based
- persistence becomes an explicit synchronization step
- cross-server freshness becomes a timing concern, not a local object-model concern

### Avoid machine-local special cases

If the goal is shared synchronized storage, avoid burying server-specific path logic in feature code.

Prefer:

- one configured root
- one consistent pit location convention
- OsLib config and path helpers

## PersonPit Example for OTW / AfricaStage

This is a practical example for a small backend feature where a `person` pit stores flexible person records.

```csharp
using System;
using System.Collections.Generic;
using JsonPit;
using OsLib;
using RaiUtils;

var configuredCloudRootText = (string?)Os.Config.Cloud?.GoogleDrive
   ?? throw new InvalidOperationException("Set Cloud.GoogleDrive in ~/.config/RAIkeep/osconfig.json5 first.");
var configuredCloudRoot = new RaiPath(configuredCloudRootText);

var personPitRoot = configuredCloudRoot / "AfricaStage" / "OTW" / "person";
personPitRoot.mkdir();

var personPit = new Pit(
   pitDirectory: personPitRoot.Path,
   readOnly: false,
   autoload: true,
   backup: false);

var max = personPit["Max"] ?? new PitItem("Max");

max.SetProperty(new { Email = "max@example.org" });
max.SetProperty(new { Phone = "+27-82-000-0000" });
max.SetProperty(new { Instagram = "max.africastage" });
max.SetProperty(new { Facebook = "max.africastage.fb" });
max.SetProperty(new { ComPref = new[] { "WhatsApp", "Email" } });

personPit.Add(max);
personPit.Save();

max = personPit["Max"] ?? throw new InvalidOperationException("Max should exist after save.");
max.SetProperty(new
{
   Address = new
   {
      Street = "42 Long Street",
      City = "Cape Town",
      Province = "Western Cape",
      Country = "South Africa"
   }
});

personPit.Add(max);
personPit.Save();

var reloadedPersonPit = new Pit(
   pitDirectory: personPitRoot.Path,
   readOnly: false,
   autoload: true,
   backup: false);

var savedMax = reloadedPersonPit["Max"];
if (savedMax == null)
   throw new InvalidOperationException("Max should exist after reload.");

var savedEmail = savedMax["Email"]?.ToString();
var savedPhone = savedMax["Phone"]?.ToString();
var savedInstagram = savedMax["Instagram"]?.ToString();
var savedAddressCity = savedMax["Address"]?["City"]?.ToString();
var savedComPref = savedMax["ComPref"]?.ToObject<List<string>>() ?? new List<string>();

Console.WriteLine(savedEmail);
Console.WriteLine(savedPhone);
Console.WriteLine(savedInstagram);
Console.WriteLine(savedAddressCity);
Console.WriteLine(string.Join(", ", savedComPref));
```

This is a good starting shape for an OTW API because it keeps the model flexible:

- `Id` is the stable key
- common communication fields are simple attributes
- `ComPref` is a JSON array
- `Address` is a nested JSON object

That gives another implementation agent enough flexibility to start small without introducing a schema-migration burden too early.

## Recommended Usage Summary

If you are implementing a small JsonPit-backed feature from NuGet packages, start with this pattern:

1. Resolve a stable shared root with OsLib.
2. Build the pit path with `RaiPath`.
3. Open the pit once and keep it in a long-lived singleton/static server component.
4. Use `PitItem.SetProperty(new { ... })` for normal updates.
5. Read current items from memory with `pit["Id"]` or `pit.AllUndeleted()`.
6. Call `Save()` at useful persistence boundaries.
7. Treat cross-server behavior as asynchronous persistence with eventual durability, not real-time synchronization.

## See Also

- [README.md](README.md)
- [JsonPit.cs](JsonPit.cs)
- [PitFile.cs](PitFile.cs)
- [JsonPit.Tests/UnitTests.cs](JsonPit.Tests/UnitTests.cs)
- [JsonPit.Tests/JsonPitRealWorldIntegrationTests.cs](JsonPit.Tests/JsonPitRealWorldIntegrationTests.cs)