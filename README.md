# ZipEntryAccess

.NET library designed to efficiently retrieve specific entries from a ZIP archive without extracting the entire archive or iterating through all entries. This library leverages modified code from Microsoft's System.IO.Compression.

## Features

- Efficiently locate and retrieve specific ZIP entries by name.
- Avoid loading all entries into memory.
- Supports custom encoding for entry names and comments.
- Provides a straightforward API for accessing and decompressing ZIP entries.

## Installation

You can install the FastZipEntry package via NuGet:

```sh
dotnet add package FastZipEntry
```

# Usage

## Creating an Instance

To use FastZipEntry, create an instance by passing a stream of the ZIP file and an optional encoding for entry names and comments.

```csharp
using System.IO;
using System.Text;
using EfficientZipReader;

// Example: Open a ZIP file and create a ZipEntryAccess instance
using FileStream zipFileStream = new FileStream("path/to/your.zip", FileMode.Open, FileAccess.Read);
ZipEntryAccess zipEntryAccess = new ZipEntryAccess(zipFileStream, Encoding.UTF8);
```

## Retrieving an Entry

To retrieve a specific entry from the ZIP archive, use the RetrieveZipEntry method by providing the name of the entry and an optional StringComparison parameter.

```csharp
// Example: Retrieve a specific entry from the ZIP file
string entryName = "desired_entry.txt";
ZipEntry? entry = zipEntryAccess.RetrieveZipEntry(entryName, StringComparison.OrdinalIgnoreCase);

if (entry != null)
{
    // Use the entry (e.g., decompress it)
    using Stream entryStream = entry.Open();
    using FileStream outputStream = new FileStream("path/to/extracted/desired_entry.txt", FileMode.Create, FileAccess.Write);
    entryStream.CopyTo(outputStream);
}
else
{
    Console.WriteLine("Entry not found.");
}
```

## Decompressing an Entry

Once you have retrieved a ZipEntry, you can decompress it using the Open method, which returns a Stream for the entry's contents.

```csharp
// Example: Decompress and read the content of the retrieved entry
using Stream entryStream = entry.Open();
using StreamReader reader = new StreamReader(entryStream);
string content = reader.ReadToEnd();
Console.WriteLine(content);
```

License
This project is licensed under the MIT License. See the LICENSE file for more details.

Contributing
Contributions are welcome! Please open an issue or submit a pull request for any changes or improvements.

Acknowledgments
This library is based on modified code from the (Microsoft System.IO.Compression)[https://github.com/dotnet/runtime/tree/9daa4b41eb9f157e79eaf05e2f7451c9c8f6dbdc/src/libraries/System.IO.Compression/src/System/IO/Compression] repository.