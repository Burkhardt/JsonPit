## Requirements for JsonPit

### Basic Ideas
- Store all information in the file system.
- Structure data (text and numbers) in JSON files.
- Store images in formats like webp, png, jpeg, etc.
- Store videos in formats like ogg, mov, etc.

### Structured Data
- No schema, just JSON.
- Stored in JsonPit files.
- Collections of objects.
- The name of the collection is the file name.

### Types of JSON Files
#### Single Item Files for Changes
- Written when CRUD operations are applied to "read-only" JsonPit files.
- Merged into a JsonPit file after synchronization to the master server.

#### JsonPit Files
- Can be synchronized to all CloudDrive replications like other files.
- Readable by the library on all synchronized copies.
- Only the master server can open for update and collect change files before any operation.
- The timestamp indicates the last update time.
- Change files arriving at the master's cloud storage will be processed with the next update/open operation.
- Only the master server deletes change files from CloudStorage.
- Cloud storage synchronizes change files between servers and JsonPit files.
- Only the main server can update, delete, or move JsonPit files.
- Other servers read JsonPit into memory and apply changes from change files, creating new change files for modifications.
- The main server should stay online and process change files regularly.

### Naming Conventions
- JsonPit is a folder named `[name]`.
- JsonPit file for structured data: `[name]/[name]_pit.json`.
- Change files: `[name]/[name]_[servername]_[systemTimeInSeconds].json`.
- Media files and documents: `[name]/[name]_[serverName]_[systemTimeInSeconds].[extension]` with extensions being one of `[webp|png|jpeg|ogg|mov|xls|xlsm|docx|pdf]`.

### Values
- Each element has a name and a timestamp.
- Values can be any JSON object.
- Values stored in a history list, ordered by date, youngest first.
- History length limited to prevent file size explosion.
- Method to read value history for time series data.

### Main Server Fail-Safe
- Failover mechanism for incapacitated main server.
- `JsonPitServers_pit.json` contains elements for each JsonPit and their main server.
- Algorithm to refresh `JsonPitServers_pit.json` to prevent deadlocks.

### Performance
- Read access processed in-memory for speed.
- Write access executed in-memory, creating change files in CloudStorage.
- Partly eventual persistence for data stored in JsonPit.

### Non-Structured Data
- Videos, images, and documents stored in CloudStorage directory structure.
- Path references used in JsonPit files.
- Consider copying media files into JsonPit to avoid orphaned references.

### Avoiding Cloud Space Explosion
- Prune history within elements to avoid high space consumption.
- GarbageCollector process may be necessary to handle orphaned references.
- Copy referenced media files into JsonPit for better management.

### Accessibility
- Ensure at least one server instance runs on the same CloudSpace account.
- Develop a server as a deployable Docker image for various platforms.
- Provide a NuGet package.
- Create a project website for documentation and support.

By adhering to these requirements, JsonPit will efficiently manage structured and unstructured data across multiple servers with robust synchronization and failover mechanisms.