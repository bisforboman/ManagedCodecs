// Starts the .NET runtime under Node with the command line's arguments, and gives Program.cs a way to read files.
import { dotnet } from './_framework/dotnet.js';
import { readFileSync } from 'node:fs';

const { setModuleImports } = await dotnet.withApplicationArguments(...process.argv.slice(2)).create();
setModuleImports('main.mjs', { readFile: path => readFileSync(path).toString('base64') });
await dotnet.run();
