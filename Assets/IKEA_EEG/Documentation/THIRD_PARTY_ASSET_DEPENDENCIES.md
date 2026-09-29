# IKEA_EEG — third-party art dependencies (Area A and Area B)

> **The Area A and Area B visuals are NOT fully contained in this Git repository.**
>
> They reference files from three third-party Unity Asset Store packs that `.gitignore`
> excludes. A fresh clone opens with those props, materials and textures missing. This
> document lists exactly which files are needed, where they came from, and how to restore them.

Audit date: 2026-09-29, against `main` at `5649a48`.

## How this list was produced

The list is a GUID closure, not a folder listing. Every `guid:` reference was followed
starting from `Assets/IKEA_EEG/Scenes/IKEA_EEG_AreaA.unity` and
`Assets/IKEA_EEG/Scenes/IKEA_EEG_AreaB.unity`:

- scene → prefab → nested prefab → mesh / material / physics material
- material → shader / texture

References stored inside `.meta` files were followed too. A pack file is listed here only if
that chain reaches it. Having a folder on disk does not make it a dependency.

Pack names, publishers, versions and product IDs come from two places:
- the Asset Store package headers in the local Unity download cache
  (`%APPDATA%\Unity\Asset Store-5.x\`)
- the `AssetOrigin` blocks Unity wrote into the imported `.meta` files

---

## 1. Required third-party packs

| Folder in `Assets/` | Asset Store title | Publisher | Version | Asset Store product ID | Gitignored? |
|---|---|---|---|---|---|
| `JeffamazedDev/` (contains `HouseholdPropsPack/`) | 3D Low-Poly \| Modular Household Starter Pack | JeffamazedDev | 1.0.0 (upload 985228, published 30 Aug 2026) | 390864 | **Yes** |
| `Urban_Props_Pack_Rozity/` | Urban Props Pack | Rozity | 1.0 (upload 925114, published 29 May 2026) | 377016 | **Yes** |
| `YughuesFreeArchitecturalMaterials/` | Yughues Free Architectural Materials | Nobiax / Yughues | 1.0 (upload 743317, published 24 Mar 2025) | 13234 | **Yes** |

**Source URLs.** None of the packs ships a store URL. The product IDs above are exact, and an
Asset Store page can normally be reached as `https://assetstore.unity.com/packages/slug/<product ID>`,
for example `…/slug/390864`. That URL form has **not** been checked online for these three packs.
The most reliable route is the Unity Package Manager's **My Assets** list for the account that
downloaded them.

**Not required:** `Assets/Realistic Metal Texture/` (Biostart, product 305877, 115 files,
about 1.17 GB) is also gitignored. Nothing in Area A or Area B references it.

Each pack folder and its sibling folder `.meta` (for example `Assets/JeffamazedDev.meta`) are
listed in `.gitignore`. None of their files has ever been committed.

---

## 2. Area A dependencies — Urban_Props_Pack_Rozity

**Entry point.** `Assets/IKEA_EEG/Prefabs/AreaA_Visuals.prefab` (tracked) is instanced by
`IKEA_EEG_AreaA.unity`. It references 7 pack files directly: 3 FBX and 4 materials. The pack's
own prefabs in `Prefabs/URP/` are **not** referenced, because the builder unpacks them
completely. The builder still loads them by path, though (see §4).

**21 required files** (of 124 in the pack; about 28 MB of 149 MB). All paths are under
`Assets/Urban_Props_Pack_Rozity/`.

| File | Dependency |
|---|---|
| `Models/SM_Park_Bench_02.fbx` | prefab → mesh |
| `Models/SM_StreetLight_Modern_01.fbx` | prefab → mesh |
| `Models/SM_TrashCan_01.fbx` | prefab → mesh |
| `Material/URP/M_Park_Bench_02.mat` | prefab → material |
| `Material/URP/M_StreetLight_Modern_02.mat` | prefab → material |
| `Material/URP/M_StreetLight_Modern_Glass_02 1.mat` (the name contains a space; it uses no textures) | prefab → material |
| `Material/URP/M_TrashCan_01.mat` | prefab → material |
| `Textures/T_Bench2.png`, `T_Bench2_AO.png`, `T_Bench2_Height.png`, `T_Bench2_Metallic.png`, `T_Bench2_Normal.png` | material → texture |
| `Textures/T_StreetLight_Modern_02_Base_color.png`, `T_StreetLight_Modern_02_AO.png`, `T_StreetLight_Modern_02_Normal.png`, `T_StreetLight_Modern_02Metallic.png` | material → texture |
| `Textures/T_TrashCan_01_Base_color.png`, `T_TrashCan_01_AO.png`, `T_TrashCan_01_Height.png`, `T_TrashCan_01_Normal.png`, `T_TrashCan_01Metallic.png` | material → texture |

### Local-only texture import overrides

> **The 512 texture caps exist only on the original machine.**
>
> `AreaAEnvironmentBuilder.ApplyPropTextureBudget()` sets `maxTextureSize: 512` in the
> `.meta` of each of the 14 textures above. The store package ships them at
> `maxTextureSize: 2048`. The folder is gitignored, so these edits live **only** in the local
> `.meta` files.

A re-download from the Asset Store restores the 2048 defaults. The scene still renders, but the
props use roughly 16× the texture memory until **IKEA_EEG ▸ Visuals ▸ Rebuild Area A
Environment** is run again, which reapplies the cap. Restoring from a private backup that kept
the `.meta` files preserves the cap.

Apart from the cap and Unity's own `AssetOrigin` bookkeeping, every required file and `.meta`
is byte-identical to the store package.

---

## 3. Area B dependencies

### 3a. JeffamazedDev / HouseholdPropsPack

**Direct dependency: 24 prefabs referenced by `IKEA_EEG_AreaB.unity`** (scene → prefab). All are
under `Assets/JeffamazedDev/HouseholdPropsPack/Prefabs/Decoration/`:

| Area | Prefab (path relative to `Prefabs/Decoration/`) |
|---|---|
| Dining room | `DiningRoom/DEC_DiningPendantLight.prefab`, `DiningRoom/DEC_DiningTable.prefab` |
| Kitchen, general | `Kitchen/General/Bowls/DEC_CerealBowl.prefab`, `Kitchen/General/CeramicPlates/DEC_CeramicDinnerPlate.prefab`, `Kitchen/General/Cookware/CookingPot/DEC_CookingPot.prefab`, `Kitchen/General/Cookware/DEC_NonStickPan.prefab`, `Kitchen/General/DEC_KitchenTrashCan.prefab`, `Kitchen/General/Drinkware/CoffeeMug/DEC_CoffeeMug.prefab`, `Kitchen/General/Drinkware/RegularDrinkingGlass/DEC_RegularDrinkingGlass_EMPTY.prefab` |
| Kitchen cabinets | `Kitchen/KitchenCabinets/DEC_KitchenCabinetDouble.prefab`, `Kitchen/KitchenCabinets/DEC_KitchenCabinetSingle.prefab` |
| Kitchen counters | `Kitchen/KitchenCounters/DEC_KitchenCounterCorner.prefab`, `Kitchen/KitchenCounters/DEC_KitchenCounterDouble.prefab`, `Kitchen/KitchenCounters/DEC_KitchenCounterFourDrawers.prefab`, `Kitchen/KitchenCounters/DEC_KitchenCounterSingle.prefab`, `Kitchen/KitchenCounters/KitchenCounterOven/DEC_KitchenCounterOven.prefab`, `Kitchen/KitchenCounters/KitchenCounterSink/DEC_KitchenCounterSink.prefab`, `Kitchen/KitchenCounters/KitchenCounterSink/DEC_DryingRack.prefab` |
| Kitchen electronics | `Kitchen/KitchenElectronics/DEC_Microwave.prefab` |
| Living room | `LivingRoom/DEC_CoffeeTable.prefab`, `LivingRoom/Sofas/DEC_SofaPillow.prefab`, `LivingRoom/Sofas/SofaThreeSeats/DEC_SofaThreeSeats.prefab` |
| Storeroom | `Storeroom/CardboardBoxes/DEC_CardboardBox_CLOSED.prefab`, `Storeroom/CardboardBoxes/DEC_CardboardBox_OPENED.prefab` |

> **The 24 prefabs are not enough.** They pull in another **154 indirect files**, all equally
> required. The pack needs **178 files in total** (of 252; about 2.4 MB of 3.1 MB).

The indirect files, all under `Assets/JeffamazedDev/HouseholdPropsPack/`:

- **6 nested prefabs** (prefab → prefab): `DEC_CookingPotBody`, `DEC_CookingPotLid`,
  `DEC_KitchenCounterOvenBody`, `DEC_OvenTray`, `DEC_KitchenCounterSinkBody`,
  `DEC_SofaThreeSeatsBody` (each next to its parent prefab).
- **21 FBX meshes** (prefab → mesh), under `Meshes/Main/`:
  - `DiningRoom/`: `DiningPendantLight.fbx`, `DiningTable.fbx`
  - `Kitchen/General/`: `Bowls/CerealBowl.fbx`, `CeramicPlates/CeramicDinnerPlate.fbx`,
    `Cookware/CookingPot.fbx`, `Cookware/NonStickPan.fbx`, `Drinkware/CoffeeMug.fbx`,
    `Drinkware/RegularDrinkingGlass.fbx`, `KitchenTrashCan.fbx`
  - `Kitchen/KitchenCabinets/`: `KitchenCabinetDouble.fbx`, `KitchenCabinetSingle.fbx`
  - `Kitchen/KitchenCounters/`: `KitchenCounterCorner.fbx`, `KitchenCounterDouble.fbx`,
    `KitchenCounterFourDrawers.fbx`, `KitchenCounterOven.fbx`, `KitchenCounterSingle.fbx`,
    `KitchenCounterSink.fbx`
  - `Kitchen/KitchenElectronics/`: `Microwave.fbx`
  - `LivingRoom/`: `CoffeeTable.fbx`, `Sofas/SofaThreeSeats.fbx`
  - `Storeroom/`: `CardboardBox.fbx`
- **107 collider hull meshes** (prefab → MeshCollider mesh; 75 `.mesh` plus 32 `.asset`). Every
  file in these 21 folders is required. They sit under `Meshes/Complex/Concave/` or
  `Meshes/Complex/Convex/`:
  - **Concave:** `CeramicDinnerPlateMesh_Hulls`, `CerealBowlMesh_Hulls`, `CoffeeMugMesh_Hulls`,
    `CookingPotLidMesh_Hulls`, `CookingPotMainMesh_Hulls`, `DiningPendantLightDomeMesh_Hulls`,
    `DryingRackMesh_Hulls`, `NonStickPanMesh_Hulls`, `RegularDrinkingGlassMesh_Hulls`
  - **Convex:** `CeramicDinnerPlateMesh_C_Hulls`, `CerealBowlMesh_C_Hulls`,
    `CoffeeMugMesh_C_Hulls`, `CookingPotLidMesh_C_Hulls`, `CookingPotMainMesh_C_Hulls`,
    `CookingPotMesh_C_Hulls`, `DiningPendantLightDomeMesh_C_Hulls`, `DryingRackMesh_C_Hulls`,
    `MicrowavePlateMesh_C_Hulls`, `NonStickPanMesh_C_Hulls`,
    `RegularDrinkingGlassMesh_C_Hulls`, `SofaPillowMesh_C_Hulls`
- **7 materials and 1 shader** (prefab → material → material / shader), under `Materials/`:
  - `Main/MAT_CommonMaster_Opaque.mat`
  - `Main/MAT_CommonMaster_Transparent_2.mat`, `_5.mat`, `_20.mat`, `_70.mat`, `_90.mat`
  - `Main/MAT_CommonMaster.mat`
  - `CommonMaster.shadergraph`
- **3 textures** (material → texture): `Textures/ImphenziaPixPal_BaseColor.png`,
  `ImphenziaPixPal_Emission.png`, `ImphenziaPixPal_Attributes.png`.
- **9 physics materials** (prefab → physics material): `Materials/Physics/PHYMAT_{Ceramic,
  Cushion, Glass, Paper, Plastic, Rubber, Steel, Stone, Wood}_GENERAL.physicMaterial`.

The pack contains no C# scripts. Every required file and its `.meta` are byte-identical to the
store package, apart from Unity's `AssetOrigin` bookkeeping, so a clean re-import restores
exactly the same GUIDs.

The Area B dressing strips colliders from the placed props (see
`AreaBShowroomDressing.cs`). The hull meshes are still referenced by the prefab assets, though,
and a missing one produces a missing-mesh MeshCollider on the prefab.

### 3b. YughuesFreeArchitecturalMaterials

**8 required files** (of 81 in the pack; about 25 MB of 210 MB). All paths are under
`Assets/YughuesFreeArchitecturalMaterials/`.

| File | Referenced by | Dependency |
|---|---|---|
| `Materials/M_YFAM_Ceiling.mat` | `IKEA_EEG_AreaB.unity`, as a material override on the `AreaB_Visuals` prefab instance | direct |
| `Textures/T_YFAM_Ceiling_d.tga` | `M_YFAM_Ceiling.mat` and the project-owned `Assets/IKEA_EEG/Materials/M_AreaB_Ceiling.mat` | material → texture |
| `Textures/T_YFAM_Ceiling_n.tga` | same two materials | material → texture |
| `Textures/T_YFAM_Ceiling_s.tga` | same two materials | material → texture |
| `Textures/T_YFAM_BricksGray_s.tga` | same two materials (a specular-map slot) | material → texture |
| `Textures/T_YFAM_WoodFlooring_d.tga` | project-owned `Assets/IKEA_EEG/Materials/M_AreaB_Floor_Wood.mat` | material → texture |
| `Textures/T_YFAM_WoodFlooring_n.tga` | `M_AreaB_Floor_Wood.mat` | material → texture |
| `Textures/T_YFAM_WoodFlooring_s.tga` | `M_AreaB_Floor_Wood.mat` | material → texture |

`M_AreaB_Floor_Wood.mat` and `M_AreaB_Ceiling.mat` are tracked, project-owned **copies** of
Yughues materials. They still point at the pack's textures, so they do not remove the dependency.

> **The URP materials come from a second, nested package.** The Asset Store package imports
> `M_YFAM_Ceiling.mat` with the **built-in** Standard (Specular) shader, which renders magenta in
> this URP project. The URP version the project uses comes from the nested package
> `Assets/YughuesFreeArchitecturalMaterials/YughuesFreeArchitecturalMaterials_URP.unitypackage`,
> which ships inside the pack.

That nested package overwrites the materials with URP Lit versions under the same GUIDs. The
local `M_YFAM_Ceiling.mat` matches the nested URP version, apart from Unity's automatic upgrade
(`version: 7 → 10`, `MOTIONVECTORS` pass, `m_AllowLocking`). The seven textures are
byte-identical to the store package.

The ceiling materials also reference a `_ParallaxMap` texture (GUID
`1f80a16c2ebdfc149aee0be80baf16bc`) that does not exist, not even in the vendor package. The
parallax keyword is off, so this does not affect rendering. It is pre-existing and harmless.

---

## 4. Fresh-clone behavior

A fresh `git clone` contains **no files** from any of the three packs.

### What disappears

**Area A** (via `AreaA_Visuals.prefab`):
- both street lights (`StreetLight_Left` / `StreetLight_Right`)
- the park benches
- the trash cans

Their renderers lose their mesh and materials. Everything built from project-owned materials
stays: facade, pavement, asphalt, skyline, signage.

**Area B** (via `IKEA_EEG_AreaB.unity`):
- **All 24 showroom props become missing prefab instances.** That covers the kitchen counters
  and cabinets, oven, sink, microwave, dining table and pendant light, sofa and pillow, coffee
  table, tableware and cookware, trash can, and cardboard boxes. Unity shows each one as a
  red "Missing Prefab" placeholder.
- **The wood floor and the ceiling lose their textures.** The materials are still there (the
  project-owned copies, or a missing-material slot for the `M_YFAM_Ceiling` override), so both
  surfaces render flat or untextured.

**Not affected:** the experiment's own objects in both scenes (chairs, UI, spawn points,
scene contexts, walls), TextMesh Pro (tracked) and the Unity packages (restored from
`Packages/manifest.json`).

### What the builders do if a pack is missing

The editor builders load pack assets **by path or folder**, not by GUID. When a pack is absent,
**each one logs a warning and carries on.** None of them stops.

| Builder / menu | Pack path it reads | Behavior when the pack is absent |
|---|---|---|
| `AreaAEnvironmentBuilder` — **IKEA_EEG ▸ Visuals ▸ Rebuild Area A Environment** | `Assets/Urban_Props_Pack_Rozity/Prefabs/URP/<name>.prefab` and `…/Textures` | Logs `Prop prefab not found: … Area A will build without it.` for each prop and continues. Rebuilds `AreaA_Visuals` (and the saved `AreaA_Visuals.prefab`) **without the props**. The texture budget step silently does nothing. |
| `AreaBEnvironmentBuilder` — **IKEA_EEG ▸ Visuals ▸ Rebuild Area B Environment** | `Assets/YughuesFreeArchitecturalMaterials/Materials/M_YFAM_WoodFlooring.mat`, `M_YFAM_Ceiling.mat` | Logs `Area B: source material not found at …` and does not create or refresh the tiled floor/ceiling copies. |
| `AreaBShowroomDressing` — **IKEA_EEG ▸ Visuals ▸ Dress Area B Showroom** (and `DressFromCommandLine`) | `FindAssets` over `Assets/JeffamazedDev` and `Assets/YughuesFreeArchitecturalMaterials` | Logs `Art pass: prefab '…' not found in Assets/JeffamazedDev; that piece was skipped.` for each prop, and silently skips the ceiling re-material. **The batch entry point saves the scene afterwards.** |

> **Danger: a rebuild on a machine without the packs overwrites the committed visuals.**
> Running these builders there replaces the committed art with a bare version, and only
> warnings are logged:
> - **Rebuild Area A Environment** writes `AreaA_Visuals.prefab` to disk immediately, before the
>   scene is even saved.
> - **The Area B dressing batch entry point** saves `IKEA_EEG_AreaB.unity`.
>
> **Do not run the visual builders on a clone until the packs are restored (§5).**

---

## 5. Recovery procedure

Do this with Unity **closed** until step 3.

1. **Restore the packs.** Use one of the following, in order of preference.
   - **a. From the private backup (§6). This is preferred.** Copy the three folders **and** their
     three sibling folder `.meta` files into `Assets/`, keeping every `.meta` next to its asset.
     This restores exact GUIDs **and** the local 512 texture caps.
   - **b. From the Asset Store**, using the same Unity account (Package Manager ▸ My Assets):
     1. Import **3D Low-Poly | Modular Household Starter Pack** 1.0.0 (JeffamazedDev).
     2. Import **Urban Props Pack** 1.0 (Rozity).
     3. Import **Yughues Free Architectural Materials** 1.0 (Nobiax / Yughues), **then** open
        `Assets/YughuesFreeArchitecturalMaterials/YughuesFreeArchitecturalMaterials_URP.unitypackage`
        and import it too. That second import replaces the built-in materials with the URP
        ones, under the same GUIDs.

     Keep the default import paths; do not move or rename anything. GUIDs match the committed
     references only for these exact versions, which was verified against the cached 2026
     downloads. A newer pack version may change GUIDs or paths.
2. **Do not let Unity regenerate `.meta` files.** A pack folder copied **without** its `.meta`
   files gets new GUIDs, and every scene and prefab reference breaks. If that happens, delete the
   folder and restore it again with its `.meta` files.
3. **Open the project in Unity** and let the import finish.
4. **Validate the scenes.**
   - Run **IKEA_EEG ▸ Visuals ▸ Validate Area A Environment** and **Validate Area B
     Environment**.
   - Open `IKEA_EEG_AreaA.unity` and `IKEA_EEG_AreaB.unity`. Check the Console for
     missing-prefab or missing-reference errors.
5. **Check references by eye.**
   - The Hierarchy should show no red "Missing Prefab" entries in Area B.
   - Select `AreaA_Visuals` and a few Area B props. No `None (Mesh)`, `Missing (Material)` or
     magenta surfaces.
   - Select `M_YFAM_Ceiling`, `M_AreaB_Ceiling` and `M_AreaB_Floor_Wood`. The shader should be
     `Universal Render Pipeline/Lit` and all texture slots filled (except the known dangling
     `_ParallaxMap`).
6. **After a store re-download only:** check the texture import size of a Rozity texture such as
   `T_Bench2.png`. If it shows 2048, the local cap is gone. Rerunning **Rebuild Area A
   Environment** reapplies it, but that is a scene rebuild and should be treated as one.
7. Confirm `git status` shows **no** changes to tracked scenes, prefabs or materials. A restore
   should only add ignored files.

---

## 6. Private backup recommendation

Back up these six paths **outside Git**, for example on a private drive or in a private archive:

```
Assets/JeffamazedDev/
Assets/JeffamazedDev.meta
Assets/Urban_Props_Pack_Rozity/
Assets/Urban_Props_Pack_Rozity.meta
Assets/YughuesFreeArchitecturalMaterials/
Assets/YughuesFreeArchitecturalMaterials.meta
```

> **Keep every `.meta` file.** They hold the GUIDs the committed scenes and prefabs point to,
> and (for Rozity) the local 512 texture caps. A backup of the art files without their `.meta`
> files cannot restore the scenes.

- **Back up whole folders, not only the required files.** They are small apart from the
  textures (about 360 MB combined), and a complete folder keeps its own internal references
  intact.
- **Also keep the original store downloads.** They are in
  `%APPDATA%\Unity\Asset Store-5.x\{JeffamazedDev, Rozity, Nobiax Yughues}\…\*.unitypackage`,
  in case a pack is withdrawn from the store.
- **`Assets/Realistic Metal Texture/` does not need backing up** for Area A or Area B.
- **Refresh the backup whenever a builder changes pack import settings**, such as the Rozity
  texture cap.

---

## 7. Licensing warning

> **Do not commit these packs' raw files (meshes, textures, prefabs, materials) to a public
> repository**, and do not share them outside the licensed team, unless the pack's license
> explicitly allows it.

- **JeffamazedDev (HouseholdPropsPack):** `Documentation/LICENSE.txt` says, word for word:
  *"You cannot resell, redistribute, or share these raw asset files (meshes, textures, prefabs)
  on any other website or marketplace."* Anyone else should get the pack from the original
  download link.
- **Rozity and Nobiax / Yughues** are Unity Asset Store packs, covered by the Asset Store EULA.
  That EULA generally does not allow redistributing assets in source form. Check the current
  terms before any sharing.

This is also why copying the required files into `Assets/IKEA_EEG` is **not** the recommended
fix. It would put licensed raw files under version control. A copy would also get new GUIDs, so
every scene and prefab reference would have to be rewired.

---

## 8. Summary

| Pack | Area A | Area B | Required files | Git tracked? | Fresh clone safe? |
|---|---|---|---|---|---|
| JeffamazedDev / HouseholdPropsPack 1.0.0 | No | **Yes** (24 direct prefabs) | 178 of 252 | No (gitignored) | **No** |
| Urban_Props_Pack_Rozity 1.0 | **Yes** (via `AreaA_Visuals.prefab`) | No | 21 of 124 | No (gitignored) | **No** (and the 512 texture caps are local-only) |
| YughuesFreeArchitecturalMaterials 1.0 (+ nested URP package) | No | **Yes** (ceiling, wood floor) | 8 of 81 | No (gitignored) | **No** |
| Realistic Metal Texture 1.0 | No | No | 0 of 115 | No (gitignored) | Not needed |
| TextMesh Pro essentials (`Assets/TextMesh Pro`) | Yes | Yes | 4 | Yes | Yes |
| Unity packages (URP, uGUI, XR Interaction Toolkit, Shader Graph) | Yes | Yes | via `Packages/manifest.json` | Yes (manifest) | Yes |
