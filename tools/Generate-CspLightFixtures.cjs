// Development-only Lua oracle. The optional runtime stays in ignored artifacts.
// Install: pnpm --dir artifacts/csp-reference add fengari-node-cli@0.1.0 --ignore-scripts
const fs = require('node:fs');
const path = require('node:path');
const runtime = path.resolve(process.argv[2] || 'artifacts/csp-reference/node_modules/fengari-node-cli/package.json');
const resolveFromRuntime = require('node:module').createRequire(fs.realpathSync(runtime));
const { lua, lauxlib, lualib, to_luastring, to_jsstring } = resolveFromRuntime('fengari');
const state = lauxlib.luaL_newstate();
lualib.luaL_openlibs(state);
lua.lua_pushstring(state, to_luastring(fs.readFileSync('tests/Reference/Csp/dualsense_lights.lua', 'utf8')));
lua.lua_setglobal(state, to_luastring('CSP_DUALSENSE_SOURCE'));
const status = lauxlib.luaL_dofile(state, to_luastring('tests/Reference/Csp/generate-fixtures.lua'));
if (status !== lua.LUA_OK) {
  process.stderr.write(to_jsstring(lua.lua_tostring(state, -1)) + '\n');
  process.exitCode = 1;
}
