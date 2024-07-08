## Getting Started with JsonPit and PitItem

### Overview
JsonPit is a lightweight file-based storage system for structured data. This guide provides an example of how to create and manipulate a JsonPit with `PitItem` objects, showing how to add properties to items, store them, and retrieve them.

### Prerequisites
- Ensure you have the necessary dependencies installed, such as `Newtonsoft.Json` and `OsLib`.

### Creating and Using a JsonPit

#### Step-by-Step Example

1. **Initialize the JsonPit**:
   - Create a `Pit` instance pointing to a specific directory.
   ```csharp
   var pit = new Pit(pitDirectory: Os.CloudStorageRoot + "ObjectPit/", readOnly: false);
   ```

2. **Create a PitItem**:
   - Instantiate a `PitItem` and set its properties.
   ```csharp
   var pitItem = new PitItem("RSB");
   pitItem.SetProperty(new { Age = 60 });
   pitItem.SetProperty(new { Children = 7 });
   pitItem.SetProperty(new { Kids = new[] { "Nina", "Hannah", "Vuyisile", "Kilian", "Laura", "Mbali", "Logan" } });
   pit.Add(pitItem);
   ```

3. **Add Another PitItem**:
   - You can add more items with different properties.
   ```csharp
   var pitItem2 = new PitItem("Nomsa", new { 
       Age = 52, 
       Children = 7, 
       Kids = new[] { "Nina", "Hannah", "Vuyisile", "Kilian", "Laura", "Mbali", "Logan" } 
       });
   pit.Add(pitItem2);
   ```

4. **Save the Pit**:
   - Save the pit to persist the changes.
   ```csharp
   pit.Save();
   ```

5. **Retrieve Items from the Pit**:
   - Fetch and verify the stored items.
   ```csharp
   var item = pit.Get("RSB");
   var item2 = pit["Nomsa"];
   
   // Validate properties
   var Name = item["Name"]?.ToString();
   Assert.Equal("RSB", Name);
   var Age = item["Age"];
   Assert.Equal(60, Convert.ToInt16(Age));
   var Children = item["Children"];
   Assert.Equal(7, Convert.ToInt16(Children));
   var Kids = item["Kids"]?.ToObject<List<string>>();
   var Kid6 = Kids?[6];
   Assert.Equal("Logan", Kid6);
   var Kids2 = item2["Kids"]?.ToObject<List<string>>();
   var Kid5 = Kids2?[5];
   Assert.Equal("Mbali", Kid5);
   ```

### Explanation
- **Pit Initialization**: Creates a new `Pit` in the specified directory.
- **PitItem Creation**: Adds properties to the `PitItem` object.
- **Adding Items**: Adds the `PitItem` to the `Pit`.
- **Saving**: Ensures all changes are written to the storage.
- **Retrieving**: Fetches and checks properties of stored items.

### Conclusion
This example demonstrates the fundamental operations of creating, adding properties, saving, and retrieving items using JsonPit and PitItem. By following these steps, you can effectively manage structured data in a file-based storage system.