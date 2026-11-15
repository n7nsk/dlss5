# Third-party notices

This project is a community fork of ReShade with an optional DLSS 5 installation workflow. It is not affiliated with, endorsed by, or sponsored by NVIDIA, ReShade, or the RenoDX authors.

## ReShade

The ReShade source code in this repository is Copyright (C) 2014 Patrick Mours and is licensed under the BSD 3-Clause license. See [LICENSE.md](LICENSE.md).

Some source files and bundled dependencies may have additional license terms. Their original notices remain applicable.

## NVIDIA DLSS and Streamline components

Release assets containing `nvngx_*.dll` and `sl*.dll` are proprietary NVIDIA components obtained from NVIDIA DLSS/Streamline distributions. They are not part of the ReShade source license. Use and redistribution are subject to the applicable NVIDIA SDK/license terms and accompanying notices.

NVIDIA DLSS license: <https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt>

NVIDIA Streamline: <https://github.com/NVIDIA-RTX/Streamline>

The installer downloads these files unchanged from the project's GitHub Release and verifies their SHA-256 hashes. This project does not claim ownership of NVIDIA binaries.

## RenoDX addon

`renodx-dlss.addon64` is a third-party binary. It is not authored by ReShade or NVIDIA. This repository does not grant additional rights to this binary; users should verify the original author's license and redistribution terms before mirroring or redistributing it.

## Community DLSS-NR compatibility binary

`nvngx_dlssnr_compat-fp16-310.9.16-cuda13.4.3.dll` is a third-party compatibility binary supplied for RTX 20/30/40 installations. It is distributed unchanged as a release asset and is not covered by the BSD 3-Clause license for the ReShade source code.

## Trademarks

NVIDIA, GeForce, RTX, DLSS, and related marks are trademarks of NVIDIA Corporation. ReShade is a project by its respective authors. Product names are used only for identification and compatibility information.
