### Requirements Coverage Analysis

**Legend:**
- ✅ Requirement is met.
- ◯ Requirement is partially met or implicit.
- ⬜️ Requirement is not met.

#### Basic Ideas
- **Store all information in the file system:** ✅
- **Structured data, text, and numbers in JSON files:** ✅
- **Images in webp, png, jpeg, etc.:** ⬜️
- **Videos in ogg, mov, etc.:** ⬜️

#### Structured Data
- **No schema, just JSON:** ✅
- **Stored in JsonPit files:** ✅
- **Collections of objects:** ✅
- **Name of the collection is the file name:** ◯ (implicit in file handling)

#### Types of JSON Files
- **Single item files for changes:** ◯
    - Can be written when CRUD operations are applied to "read only" JsonPit files: ⬜️
    - Can be merged into a JsonPit file after file synchronization to the master server: ✅ (in `MergeChanges`)
- **JsonPit files:**
    - Can be synchronized to all CloudDrive replications just like all other files: ⬜️
    - Can be read by the library on all synchronized copies of the JsonPit: ◯
    - If opened on the master, the only server that can open for update, all change files will be collected and merged into the file before any operation happens: ✅
    - The timestamp of the JsonPit file tells the last time the JsonPit was updated: ✅
    - Change files arriving at the master server’s cloud storage copy will cause updates of the JsonPit with the next update/open operation performed: ◯
    - Change files will be deleted from the CloudStorage only by the master server: ✅
    - The Cloud storage will take care of synchronizing the change files (including when deleted) between all servers and the JsonPit files: ◯
    - Only the main server can update, delete, or move JsonPit files: ◯
    - When opened by any other server than the main server, a JsonPit will be first read into memory in its entirety and then all change files will be read as well, and the memory will be updated accordingly: ✅
    - The main server should remain online and available as much as possible and take care of opening each of the JsonPit files as soon as change files start to add up: ◯ (implied in comments and methods)

#### Naming Conventions
- **JsonPit is a folder named `[name]`:** ⬜️
- **JsonPit file for structured data:** ✅
- **Change files:** ✅
- **Media files and documents:** ⬜️

#### History and Values
- **All elements have a name and a timestamp:** ✅
- **Default values kept in a list (called history), ordered by date, youngest first:** ✅
- **Limitation of the length of the history list for frequently changing values:** ◯ (using `MaxCount`)
- **Read more than just the latest value of an element stored in a JsonPit file:** ◯ (methods like `ValuesOverTime` and `ValueListsOverTime`)

#### Performance
- **Read Access to the values of a JsonPit will be processed on its in-memory representation and will therefore be fast:** ✅
- **Write Access will be executed on the current server instance in-memory, but will also generate a change file in the CloudStorage location:** ✅

#### Other than Structured Data
- **Videos, Images, and other documents stored in the CloudStorage in a certain directory structure:** ⬜️
- **Their path can be used to reference them from JsonPit files:** ⬜️

#### Cloud Space and Garbage Collection
- **Cloud Space services may swap them away or conduct lazy loading for those external files:** ⬜️
- **Pruning the history within an element in a JsonPit will create widows/orphans and a GarbageCollector process will most likely become necessary:** ⬜️
- **Copying referenced media files “into” the JsonPit:** ⬜️

#### Accessibility
- **Make sure there is at least one server instance running on the same CloudSpace account:** ◯
- **Develop a server as a Docker image, deployable to Win, macOS, Lx:** ⬜️
- **Provide NuGet package:** ⬜️
- **Have a project website:** ⬜️

### Summary of Met and Unmet Requirements

**Met Requirements:**
- ✅ Basic ideas of storing data in JSON files.
- ✅ Handling structured data without a schema.
- ✅ Timestamping elements.
- ✅ History management.
- ✅ Performance optimization for read and write operations.
- ✅ Change file mechanism and master server handling.

**Unmet or Partially Met Requirements:**
- ⬜️ Media and document storage and referencing.
- ◯ Full implementation of naming conventions.
- ◯ Full synchronization and CloudDrive handling.
- ⬜️ Garbage collection for pruning history.
- ⬜️ Docker image development, NuGet package, and project website creation.