# ForgeStore — Unturned Plugin

Rocket plugin for Unturned servers with automatic purchase delivery.

## Installation
1. Copy the plugin DLL to `Rocket/Plugins/`
2. Edit `ForgeStore.Configuration.xml`: set your `SecretKey`
3. Restart the server
4. Verify: `/forgestore info`

## Commands
| Command | Description |
|---------|-------------|
| `/forgestore secret <key>` | Set secret key |
| `/forgestore check` | Force queue check |
| `/forgestore info` | Show store info |

## Example Commands
```
p {player} experience add 1000
tp {player} vehicle 119
```
