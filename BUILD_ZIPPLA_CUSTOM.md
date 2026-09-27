# ZipPla custom build

This build adds a Windows Explorer context-menu toggle.

## Added behavior

- `Start` menu -> `Register context menu`
  - First click: registers ZipPla in Explorer's context menu.
  - Second click: removes the registration.
  - No administrator rights are required; registration is stored under the current user's registry.
- The context menu is added for:
  - folders
  - `.zip` files
  - `.rar` files
- Selecting `Open with ZipPla` launches ZipPla with the selected path.
- ZipPla's existing command-line handling opens the supplied folder/archive, and its normal location-list update inserts the opened path into the left location list.

## Build

The project targets .NET Framework 4.8 and is a classic Visual Studio/MSBuild project.

(d1: retargeted from 4.5.2. The 4.5.2 targeting pack is no longer distributed, so the project could
not be built at all without a developer pack that is no longer available for download.)

Open:

`source\ZipPla.sln`

Then build the `Release` configuration for `Any CPU`.

The executable is produced under the project's configured Release output directory.
