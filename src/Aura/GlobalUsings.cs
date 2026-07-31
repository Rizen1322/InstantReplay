// Набор using'ов на весь проект.
// Явно, а не через ImplicitUsings: временный проект, который MSBuild собирает
// для XAML (Aura_*_wpftmp.csproj), неявные using'и наследует не полностью —
// из-за этого System.IO «пропадал» в файлах ядра.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
