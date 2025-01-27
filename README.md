# ResoniteBridgeLib
A connector that lets you communicate between Resonite mods and other applications, using [MemoryMappedFileIPC](https://github.com/Phylliida/MemoryMappedFileIPC)

## Usage

First, select a folder that'll store all the current connections.

You can just use something like

```c#
string serverDirectory = 
	Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
		"YourApplication",
		"IPCConnections",
		"Servers"
	);
```

Also pick a channel name, this should be unique to your mod.

```c#
string channelName = "myModChannel";
```

### Resonite side:

Your mod needs to create a server.

```c#
ResoniteBridgeServer bridgeServer = new ResoniteBridgeServer(
	channelName,
	serverDirectory,
	(string msg) =>
{
	// this is just for debug messages, if you want them
	// you can also comment this out
	Msg("ResoniteBridgeLib logging:" + msg);
});
```

Now you can register functions to be called. For example:

```c#
string functionLabel = "TestFunction";
```

```c#
static byte[] SimpleTestFunc(byte[] inputBytes) {
	byte[] responseBytes = ...; // make response bytes
	return responseBytes;
}
```

```c#
server.RegisterProcessor(fuctionLabel, SimpleTestFunc);
```

### Other application side

```c#
ResoniteBridgeClient bridgeClient = new ResoniteBridgeClient(
	channelName,
	serverDirectory, 
	(string message) => { Debug.Log(message); } // this is for unity, modify this for your own logging
);
```

Then to send our message

```c#
byte[] inputBytes = ...; // make input bytes
int timeout = -1; // for no timeout, take as long as they want
bridgeClient.SendMessageSync(
	"TestFunction",
	encoded,
	timeout,
	out byte[] outBytes,
	out bool isError
	);
if (isError)
{
    throw new Exception(ResoniteBridgeUtils.DecodeString(outBytes));
}
else
{
	// process outBytes
}
```



## Serialization

This works using `byte[]`, but in practice it's easier to work with objects or strings.

To support this I have a few helper methods in `ResoniteBridgeUtils`.

### Strings

You can use `ResoniteBridgeUtils.EncodeString` and `ResoniteBridgeUtils.DecodeString`.

#### Objects

Because working with bytes directly is annoying, you could deserialize and serialize using Newtonsoft.Json's bson:

```c#
ExampleObject exampleObj = ...;
byte[] inputBytes = ResoniteBridgeUtils.EncodeObjectBson(exampleObj);
```

Then to decode:

```c#
ExampleObject inputObject = ResoniteBridgeUtils.DecodeObjectBson<ExampleObject>(inputBytes);
```

However, I find if you have large arrays of primitive types or structs this can be slow.

To get around this, I implemented my own serialization that is much faster (50x faster than bson for large arrays).

It's faster because it just directly copies the raw bytes of those large arrays using Marshal.

It only works for structs with `[StructLayout(LayoutKind.Sequential)]` with fields that are either:
- A: primitive types
- B: T[] of primitive types
- C: T[] of struct satisfying the above
- D: a struct with fields satisfying A-C or D

But this isn't that big of a limitation in practice.

For example, here is a valid struct:

```c#
[StructLayout(LayoutKind.Sequential)]
public struct Float3_Example
{
 public float x;
 public float y;
 public float z;
}
```

To encode, just do
```c#
Float3_Example example = new Float3_Example() {
	x=0,
	y=1,
	z=2
};
byte[] inputBytes = ResoniteBridgeUtils.EncodeObject(exampleObj);
```

Then to decode:

```c#
Float3_Example inputObject = ResoniteBridgeUtils.DecodeObject<Float3_Example>(inputBytes);
```

## Modifying Resonite

The functions you register will be occuring on a seperate thread than resonite, and will throw an error if you try and do something like add a Slot.

To fix this, I recommend following this pattern.

First, define some helpers:

```c#
public class OutputBytesHolder
{
    public byte[] outputBytes;
}

static IEnumerator<FrooxEngine.Context> ActionWrapper(IEnumerator<FrooxEngine.Context> action, System.Threading.Tasks.TaskCompletionSource<bool> completion)
{
    try
    {
        yield return FrooxEngine.Context.WaitFor(action);
    }
    finally
    {
        completion.SetResult(result: true);
    }
}

public static bool RunOnWorldThread(IEnumerator<FrooxEngine.Context> action)
{
    System.Threading.Tasks.TaskCompletionSource<bool> taskCompletionSource = new System.Threading.Tasks.TaskCompletionSource<bool>();
    Engine.Current.WorldManager.FocusedWorld.RootSlot.StartCoroutine(ActionWrapper(action, taskCompletionSource));
    return taskCompletionSource.Task.GetAwaiter().GetResult();
}
```


Now you can do something like this:

```c#
[StructLayout(LayoutKind.Sequential)]
public struct RefID_Example
{
    public ulong id;
}
static IEnumerator<FrooxEngine.Context> AddSlotFuncHelper(byte[] inputBytes, OutputBytesHolder outputBytes)
{
    // move to background thread (optional, useful if you are doing heavy stuff)
    yield return FrooxEngine.Context.ToBackground();
    // move to world thread (necessary if we want to modify the world at all)
    yield return Context.ToWorld();
    string slotName = ResoniteBridgeUtils.DecodeString(inputBytes);
    Slot resultSlot = Engine.Current.WorldManager.FocusedWorld.RootSlot.AddSlot(slotName);
    RefID_Example result = new RefID_Example()
    {
        id = (ulong)resultSlot.ReferenceID
    };
    outputBytes.outputBytes = ResoniteBridgeUtils.EncodeObject(result);
}

// this is the function that we register
public byte[] AddSlotFunc(byte[] inputBytes)
{
    OutputBytesHolder outputHolder = new OutputBytesHolder();
    RunOnWorldThread(AddSlotFuncHelper(inputBytes, outputHolder));
    return outputHolder.outputBytes;
}
```
