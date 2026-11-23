# Bundled driver payload

The x64 installer and driver are copied without modification from the official [RawAccel_v1.7.1.zip release](https://github.com/RawAccelOfficial/rawaccel/releases/tag/v1.7.1).

They are embedded into KaidenAccel.exe at build time. When the driver cannot be loaded, the app offers an **Install included driver** button. Clicking it extracts the files into a new temporary folder, verifies their SHA-256 hashes, and asks Windows to launch the installer with administrator permission. The installer is interactive. Windows must be restarted after successful installation. Closing setup does not restart the PC automatically.

The driver keeps its original filename and Microsoft Windows Hardware Compatibility Publisher signature. Kernel code and signing are unchanged. Raw Accel's MIT license and attribution are retained at the repository root and in the portable package.

SHA-256:

- `x64/installer.exe`: `B3B4948DE9470A5727390ED016507B59D2C628463E42AAF1B75E906EE04F58A9`
- `x64/driver/rawaccel.sys`: `8A62C4DEEF2774B43A7363B352EDA79897533A1080C9C26FFEFF0559E43358D7`

Before updating these assets, verify their upstream origin and driver signature, then update the hashes in `grapher/DriverSetupForm.cs` and the setup extraction test expectations.
