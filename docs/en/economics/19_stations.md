# 19. Stations and station buildings

## 19.1. Station structure

Platforms are station paths, a kind of track. Large stations have multiple platforms, and a platform may span multiple paths. A station building path (`PATH_STATION_HOUSE`) groups these into the single station recognized by the player and supplies its name and identity. Stations are distinct from buildings represented as economic agents.

## 19.2. Station buildings and platforms

A station building path represents entrance buildings and passages connecting platforms. Platforms crossed by the passage belong to the station, allowing multiple lines and platforms to be grouped. Freight-only platforms do not belong to passenger station buildings ([supply and transport](17_supply-and-transport.md)). See [station building meshes](../geometries/Q_station-building-meshes.md) for geometry and rendering.

## 19.3. Entrances and transfers

Residents walk from entrances to platforms and board or leave trains. They can transfer between platforms within a station. Stations with entrances on both sides serve both sides of the railway and provide pedestrian routes through the station building. Station position and entrances affect walkability nearby. See [public transport](18_public-transport.md) for lines and stops.

