# AMD ADLX bridge

`AmdAdlxBridge.cpp` is Accessibility Zoom's native bridge. The compiled x64
bridge is included in `bin/AmdAdlxBridge.dll` so the normal .NET build does not
require Visual Studio C++ tools.

To rebuild it, install Visual Studio's C++ Build Tools and obtain the AMD ADLX
SDK from https://github.com/GPUOpen-LibrariesAndSDKs/ADLX. Review AMD's separate
SDK license. Place its `Include`, `ADLXHelper` and `WinAPIs.cpp` in `adlx/`.
The SDK headers/helper sources are an external dependency and are not mirrored
by this repository.

From an x64 Native Tools Command Prompt, in this directory:

```bat
mkdir obj
mkdir bin
cl /nologo /std:c++17 /O2 /EHsc /MT /LD /utf-8 /DUNICODE /D_UNICODE /DNOMINMAX /DWIN32_LEAN_AND_MEAN /Iadlx\Include /Iadlx\ADLXHelper /Foobj\ /Febin\AmdAdlxBridge.dll AmdAdlxBridge.cpp adlx\ADLXHelper\ADLXHelper.cpp adlx\WinAPIs.cpp
```

The AMD driver provides the ADLX runtime on compatible systems. Third-party
SDK materials retain their original license; the project's PolyForm license
does not replace it. Independent reproducibility of the supplied native binary
has not been established.
