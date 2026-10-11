using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WukongBench;

/// <summary>Одна настройка: секция ini, ключ и значение. Ключ вида "UI:Имя" правит пару внутри UISettingData.</summary>
record Setting(string Section, string Key, string Value);

/// <summary>Набор настроек одного прохода.</summary>
record Profile(string Name, List<Setting> Settings);

/// <summary>Метрики с экрана результатов.</summary>
record Metrics(string? Avg, string? Max, string? Min, string? P5, string? Vram, string Raw);

/// <summary>Итог одного прохода.</summary>
record RunResult(Profile Profile, Metrics Metrics, string Screenshot);

/// <summary>Параметры командной строки вида --ключ значение.</summary>
class Options
{
	public string? InstallDir, Exe, Ini, StartKey, AppId;
	public int StartupDelaySec = 30, TimeoutMin = 15;
	public int? GpuScale;
	public bool Rt, Help;

	/// <summary>Разбирает аргументы командной строки.</summary>
	public static Options Parse(string[] args)
	{
		var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (var i = 0; i < args.Length; i++)
			if (args[i].StartsWith("--"))
				d[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";

		string? Get(string k) => d.TryGetValue(k, out var v) ? v : null;
		return new Options
		{
			InstallDir = Get("dir"),
			Exe = Get("exe"),
			Ini = Get("ini"),
			StartKey = Get("start-key"),
			AppId = Get("appid"),
			GpuScale = Get("gpu-scale") is { } gs ? int.Parse(gs) : null,
			StartupDelaySec = int.Parse(Get("startup-delay") ?? "30"),
			TimeoutMin = int.Parse(Get("timeout") ?? "15"),
			Rt = d.ContainsKey("rt"),
			Help = d.ContainsKey("help")
		};
	}

	public const string HelpText = @"Параметры (все необязательные):
  --dir <путь>          папка Benchmark Tool (по умолчанию ищется через Steam)
  --exe <путь>          exe бенчмарка (по умолчанию <dir>\b1_benchmark.exe)
  --ini <путь>          GameUserSettings.ini (по умолчанию <dir>\b1\Saved\Config\Windows)
  --start-key enter     клавиша запуска теста, если он не стартует сам (enter, space или буква)
  --startup-delay 30    секунд ждать после запуска перед нажатием клавиши
  --timeout 15          максимум минут на один проход
  --appid <число>       номер приложения Steam (по умолчанию берётся из манифеста)
  --gpu-scale 50        масштаб рендера GPU-теста, % (по умолчанию 100, при видеопамяти меньше 6 ГБ: 50)
  --rt                  включить трассировку лучей в GPU-тесте (для видеокарт с RT)";
}

static class Program
{
	const string Gs = "/Script/GSGameSettings.GSGameUserSettings";
	const string Sg = "ScalabilityGroups";
	const string FpsPattern = @"(?i)(\d+)\s*F\s?P\s?[S5]";

	static string iniPath = "", iniBackup = "", benchPrefix = "";

	[DllImport("user32.dll")] static extern bool SetProcessDPIAware();
	[DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
	[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
	[DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
	[DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

	/// <summary>Добавляет значения качества для девяти групп интерфейса игры.</summary>
	static void AddQuality(List<Setting> s, string level, string? texture = null)
	{
		foreach (var k in new[]
		{
			"AntiAliasing", "PostProcessing", "ShadowQuality", "TextureQuality", "FxQuality",
			"MaterialQuality", "VegetationQuality", "GlobalIllumination", "ReflectionQuality"
		})
			s.Add(new Setting(Gs, "UI:" + k, k == "TextureQuality" && texture != null ? texture : level));
	}

	/// <summary>Добавляет значения sg.* для всех групп, кроме разрешения и дальности прорисовки.</summary>
	static void AddScalability(List<Setting> s, string level, string? texture = null)
	{
		foreach (var k in new[]
		{
			"AntiAliasingQuality", "ShadowQuality", "GlobalIlluminationQuality", "ReflectionQuality",
			"PostProcessQuality", "TextureQuality", "EffectsQuality", "FoliageQuality", "ShadingQuality"
		})
			s.Add(new Setting(Sg, "sg." + k, k == "TextureQuality" && texture != null ? texture : level));
	}

	/// <summary>Профиль CPU: минимальный масштаб рендера и качество, дальность прорисовки максимальная.</summary>
	static Profile CpuProfile(int w, int h)
	{
		var s = new List<Setting>();
		foreach (var k in new[] { "DesiredScreenWidth", "LastUserConfirmedDesiredScreenWidth" })
			s.Add(new Setting(Gs, k, Math.Round(w / 3.0).ToString()));
		foreach (var k in new[] { "DesiredScreenHeight", "LastUserConfirmedDesiredScreenHeight" })
			s.Add(new Setting(Gs, k, Math.Round(h / 3.0).ToString()));
		s.Add(new Setting(Gs, "UI:ImageQuality", Math.Round(h * 0.33).ToString()));
		s.Add(new Setting(Gs, "UI:WindowFullImageQuality", "1000000"));
		s.Add(new Setting(Gs, "UI:MotionBlur", "0"));
		s.Add(new Setting(Gs, "UI:Rtx", "0"));
		s.Add(new Setting(Gs, "UI:QualityLevel", "6"));
		s.Add(new Setting(Gs, "UI:ViewDistance", "5"));
		AddQuality(s, "1");
		s.Add(new Setting(Sg, "sg.ResolutionQuality", "33.3333015"));
		s.Add(new Setting(Sg, "sg.ViewDistanceQuality", "4"));
		s.Add(new Setting(Sg, "sg.RayTracingQuality", "0"));
		AddScalability(s, "0");
		s.Add(new Setting("RayTracing", "r.RayTracing.EnableInGame", "False"));
		s.Add(new Setting("GSRenderSetting", "GSStreamingPoolSize", "384"));
		return new Profile("CPU", s);
	}

	/// <summary>Профиль GPU: максимум качества; при нехватке видеопамяти текстуры и пул потоковой подгрузки минимальные.</summary>
	static Profile GpuProfile(int w, int h, bool rt, int scale, bool lowVram)
	{
		var s = new List<Setting>();
		foreach (var k in new[] { "DesiredScreenWidth", "LastUserConfirmedDesiredScreenWidth" })
			s.Add(new Setting(Gs, k, Math.Round(w * scale / 100.0).ToString()));
		foreach (var k in new[] { "DesiredScreenHeight", "LastUserConfirmedDesiredScreenHeight" })
			s.Add(new Setting(Gs, k, Math.Round(h * scale / 100.0).ToString()));
		s.Add(new Setting(Gs, "UI:ImageQuality", Math.Round(h * scale / 100.0).ToString()));
		s.Add(new Setting(Gs, "UI:WindowFullImageQuality", "1000000"));
		s.Add(new Setting(Gs, "UI:MotionBlur", "0"));
		s.Add(new Setting(Gs, "UI:Rtx", rt ? "1" : "0"));
		s.Add(new Setting(Gs, "UI:QualityLevel", lowVram ? "6" : "5"));
		s.Add(new Setting(Gs, "UI:ViewDistance", "5"));
		AddQuality(s, "5", lowVram ? "1" : null);
		s.Add(new Setting(Sg, "sg.ResolutionQuality", scale.ToString()));
		s.Add(new Setting(Sg, "sg.ViewDistanceQuality", "4"));
		s.Add(new Setting(Sg, "sg.RayTracingQuality", "0"));
		AddScalability(s, "4", lowVram ? "0" : null);
		s.Add(new Setting("RayTracing", "r.RayTracing.EnableInGame", rt ? "True" : "False"));
		s.Add(new Setting("GSRenderSetting", "GSStreamingPoolSize", lowVram ? "384" : "640"));
		return new Profile("GPU", s);
	}

	static async Task Main(string[] args)
	{
		SetProcessDPIAware();
		var opt = Options.Parse(args);
		if (opt.Help)
		{
			Console.WriteLine(Options.HelpText);
			return;
		}

		var dir = opt.InstallDir ?? FindInstallDir() ?? Ask("Папка Benchmark Tool");
		var exe = opt.Exe ?? Path.Combine(dir, "b1_benchmark.exe");
		var ini = opt.Ini ?? Path.Combine(dir, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
		if (!File.Exists(exe)) exe = Ask("Путь к exe бенчмарка");
		if (!File.Exists(ini)) ini = Ask("Путь к GameUserSettings.ini");

		var w = ReadInt(ini, "ResolutionSizeX");
		var h = ReadInt(ini, "ResolutionSizeY");
		var prefix = Regex.Match(Path.GetFileNameWithoutExtension(exe), "^[A-Za-z0-9]+").Value;
		var appId = opt.AppId ?? FindAppId(dir);
		var vram = VramGb();
		var lowVram = vram > 0 && vram < 6;
		var scale = opt.GpuScale ?? (lowVram ? 50 : 100);
		Console.WriteLine($"Папка: {dir}\nExe: {exe}\nКонфиг: {ini}\nРазрешение экрана: {w}x{h}");
		Console.WriteLine($"Steam AppID: {appId ?? "не найден, запуск через exe"}\nВидеопамять: {vram:F1} ГБ, масштаб GPU-теста: {scale}%\n");

		KillBench(prefix);
		var backup = File.ReadAllText(ini);
		File.WriteAllText(ini + ".bak", backup);
		(iniPath, iniBackup, benchPrefix) = (ini, backup, prefix);
		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			Cleanup();
			Environment.Exit(1);
		};
		var results = new List<RunResult>();
		try
		{
			foreach (var profile in new[] { CpuProfile(w, h), GpuProfile(w, h, opt.Rt, scale, lowVram) })
			{
				Console.WriteLine($"Проход {profile.Name}...");
				ApplySettings(ini, profile);
				await Launch(exe, appId, opt.StartupDelaySec, opt.StartKey);
				var (metrics, shot) = await WaitForResults(profile.Name, prefix, opt.TimeoutMin);
				results.Add(new RunResult(profile, metrics, shot));
				KillBench(prefix);
			}
		}
		finally
		{
			KillBench(prefix);
			File.WriteAllText(ini, backup);
		}

		PrintReport(results);
	}

	/// <summary>Закрывает бенчмарк и возвращает исходный ini (при прерывании по Ctrl+C).</summary>
	static void Cleanup()
	{
		KillBench(benchPrefix);
		File.WriteAllText(iniPath, iniBackup);
	}

	/// <summary>Запрашивает значение у пользователя в консоли.</summary>
	static string Ask(string prompt)
	{
		Console.Write($"{prompt}: ");
		return Console.ReadLine()?.Trim('"', ' ') ?? "";
	}

	/// <summary>Читает целое значение ключа из ini.</summary>
	static int ReadInt(string path, string key) =>
		int.Parse(Regex.Match(File.ReadAllText(path), $"^{key}=(\\d+)", RegexOptions.Multiline).Groups[1].Value);

	/// <summary>Ищет папку установки Benchmark Tool через реестр и манифесты Steam.</summary>
	static string? FindInstallDir()
	{
		var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
		if (steam == null) return null;

		var libs = new List<string> { steam };
		var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
		if (File.Exists(vdf))
			foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
				libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

		foreach (var lib in libs)
		{
			var apps = Path.Combine(lib, "steamapps");
			if (!Directory.Exists(apps)) continue;
			foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
			{
				var text = File.ReadAllText(acf);
				var name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
				if (!name.Contains("Wukong") || !name.Contains("Benchmark")) continue;
				var dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
				return Path.Combine(apps, "common", dir);
			}
		}
		return null;
	}

	/// <summary>Находит номер приложения Steam по папке установки.</summary>
	static string? FindAppId(string dir)
	{
		var apps = Directory.GetParent(dir.TrimEnd('\\'))?.Parent?.FullName;
		if (apps == null || !Directory.Exists(apps)) return null;
		var folder = Path.GetFileName(dir.TrimEnd('\\'));
		foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
			if (Regex.IsMatch(File.ReadAllText(acf), $"\"installdir\"\\s+\"{Regex.Escape(folder)}\""))
				return Regex.Match(acf, @"appmanifest_(\d+)\.acf").Groups[1].Value;
		return null;
	}

	/// <summary>Возвращает максимальный объём видеопамяти среди адаптеров по данным реестра, ГБ (0, если неизвестно).</summary>
	static double VramGb()
	{
		using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
		if (cls == null) return 0;
		var max = 0L;
		foreach (var name in cls.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")))
		{
			try
			{
				using var k = cls.OpenSubKey(name);
				if (k?.GetValue("HardwareInformation.qwMemorySize") is long v) max = Math.Max(max, v);
			}
			catch (System.Security.SecurityException) { }
		}
		return max / 1024.0 / 1024 / 1024;
	}

	/// <summary>Записывает настройки профиля в ini, добавляя отсутствующие секции и ключи.</summary>
	static void ApplySettings(string path, Profile profile)
	{
		var lines = File.ReadAllLines(path).ToList();
		foreach (var s in profile.Settings)
		{
			var start = lines.FindIndex(l => l.Trim() == $"[{s.Section}]");
			if (start < 0)
			{
				lines.AddRange(new[] { "", $"[{s.Section}]" });
				start = lines.Count - 1;
			}
			var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("["));
			if (end < 0) end = lines.Count;

			if (s.Key.StartsWith("UI:"))
			{
				var ui = lines.FindIndex(start + 1, end - start - 1, l => l.StartsWith("UISettingData="));
				if (ui < 0) continue;
				var key = s.Key[3..];
				lines[ui] = Regex.Replace(lines[ui], $"\\(\"{Regex.Escape(key)}\",\\s*\"[^\"]*\"\\)", $"(\"{key}\", \"{s.Value}\")");
				continue;
			}

			var idx = lines.FindIndex(start + 1, end - start - 1,
				l => l.StartsWith(s.Key + "=", StringComparison.OrdinalIgnoreCase));
			if (idx >= 0) lines[idx] = $"{s.Key}={s.Value}";
			else lines.Insert(start + 1, $"{s.Key}={s.Value}");
		}
		File.WriteAllLines(path, lines);
	}

	/// <summary>Запускает бенчмарк через Steam (без окна о параметрах), выводит окно на передний план и нажимает клавишу старта.</summary>
	static async Task Launch(string exe, string? appId, int delaySec, string? key)
	{
		if (appId != null)
			Process.Start(new ProcessStartInfo($"steam://rungameid/{appId}") { UseShellExecute = true });
		else
			Process.Start(new ProcessStartInfo(exe)
			{
				WorkingDirectory = Path.GetDirectoryName(exe),
				UseShellExecute = true
			});
		await Task.Delay(delaySec * 1000);

		var game = Process.GetProcessesByName("b1-Win64-Shipping").FirstOrDefault();
		if (game != null && game.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(game.MainWindowHandle);
		if (key == null) return;

		var vk = key.ToLower() switch
		{
			"enter" => (byte)0x0D,
			"space" => (byte)0x20,
			_ => (byte)char.ToUpper(key[0])
		};
		var scan = (byte)MapVirtualKey(vk, 0);
		keybd_event(vk, scan, 0, UIntPtr.Zero);
		await Task.Delay(100);
		keybd_event(vk, scan, 2, UIntPtr.Zero);
	}

	/// <summary>Завершает все процессы бенчмарка (b1_benchmark, b1-Win64-Shipping).</summary>
	static void KillBench(string prefix)
	{
		foreach (var p in BenchProcesses(prefix))
			try { p.Kill(true); } catch { }
		Thread.Sleep(3000);
	}

	/// <summary>Возвращает запущенные процессы бенчмарка.</summary>
	static IEnumerable<Process> BenchProcesses(string prefix) =>
		Process.GetProcesses().Where(p => Regex.IsMatch(p.ProcessName, $"^{prefix}[-_]", RegexOptions.IgnoreCase));

	/// <summary>Делает снимок основного экрана.</summary>
	static Bitmap Capture()
	{
		var w = GetSystemMetrics(0);
		var h = GetSystemMetrics(1);
		var bmp = new Bitmap(w, h);
		using var g = Graphics.FromImage(bmp);
		g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
		return bmp;
	}

	/// <summary>Левая колонка экрана результатов: средний, максимальный, минимальный FPS и видеопамять.</summary>
	static Rectangle LeftColumn(Bitmap b) =>
		new(0, (int)(b.Height * 0.15), (int)(b.Width * 0.34), (int)(b.Height * 0.47));

	/// <summary>Распознаёт текст в области снимка средствами Windows OCR.</summary>
	static async Task<string> Ocr(Bitmap bmp, Rectangle area)
	{
		using var crop = bmp.Clone(area, bmp.PixelFormat);
		using var ms = new MemoryStream();
		crop.Save(ms, ImageFormat.Png);
		ms.Position = 0;
		using var stream = ms.AsRandomAccessStream();
		var decoder = await BitmapDecoder.CreateAsync(stream);
		using var soft = await decoder.GetSoftwareBitmapAsync();
		var engine = OcrEngine.TryCreateFromUserProfileLanguages()
			?? throw new InvalidOperationException("Windows OCR недоступен: установите языковой пакет");
		return (await engine.RecognizeAsync(soft)).Text;
	}

	/// <summary>Ждёт появления экрана результатов (три и более значений FPS) и разбирает его.</summary>
	static async Task<(Metrics, string)> WaitForResults(string name, string prefix, int timeoutMin)
	{
		var deadline = DateTime.Now.AddMinutes(timeoutMin);
		var lastText = "";
		while (DateTime.Now < deadline)
		{
			await Task.Delay(3000);
			if (!BenchProcesses(prefix).Any())
				throw new InvalidOperationException("Бенчмарк завершился до появления результатов (возможно, не хватило видеопамяти)");
			using var shot = Capture();
			var text = await Ocr(shot, LeftColumn(shot));
			var found = Regex.Matches(text, FpsPattern).Count;
			Console.WriteLine($"  ожидание результатов, найдено значений FPS: {found}");
			if (found < 3)
			{
				lastText = text;
				continue;
			}

			await Task.Delay(2000);
			using var final = Capture();
			var path = Path.GetFullPath($"result_{name.ToLower()}.png");
			final.Save(path, ImageFormat.Png);
			return (ParseMetrics(await Ocr(final, LeftColumn(final))), path);
		}
		using (var s = Capture()) s.Save(Path.GetFullPath($"timeout_{name.ToLower()}.png"), ImageFormat.Png);
		throw new TimeoutException($"Экран результатов не распознан за отведённое время. Последний текст OCR: {lastText}");
	}

	/// <summary>Достаёт значения по порядку: средний, максимальный, минимальный, 5-й перцентиль, видеопамять.</summary>
	static Metrics ParseMetrics(string text)
	{
		var fps = Regex.Matches(text, FpsPattern).Select(m => m.Groups[1].Value).ToList();
		var vram = Regex.Match(text, @"(\d+[.,]\d+)\s*(?:Гб|ГБ|GB|Gb)");
		string? At(int i) => i < fps.Count ? fps[i] : null;
		return new Metrics(At(0), At(1), At(2), At(3), vram.Success ? vram.Groups[1].Value : null, text);
	}

	/// <summary>Читает одно поле из WMI.</summary>
	static string Wmi(string cls, string prop) =>
		string.Join("; ", new ManagementObjectSearcher($"select {prop} from {cls}").Get()
			.Cast<ManagementBaseObject>().Select(o => o[prop]?.ToString()));

	/// <summary>Печатает отчёт в консоль и сохраняет его в report.txt.</summary>
	static void PrintReport(List<RunResult> results)
	{
		var sb = new StringBuilder();
		var ramGb = Math.Round(double.Parse(Wmi("Win32_ComputerSystem", "TotalPhysicalMemory")) / 1024 / 1024 / 1024);
		sb.AppendLine("===== ПК =====");
		sb.AppendLine($"CPU: {Wmi("Win32_Processor", "Name")} ({Environment.ProcessorCount} потоков)");
		sb.AppendLine($"GPU: {Wmi("Win32_VideoController", "Name")}");
		sb.AppendLine($"RAM: {ramGb} ГБ");
		sb.AppendLine($"ОС: {Environment.OSVersion}");

		foreach (var r in results)
		{
			var m = r.Metrics;
			sb.AppendLine($"\n===== Тест {r.Profile.Name} =====");
			sb.AppendLine($"Средний FPS: {m.Avg ?? "?"}");
			sb.AppendLine($"Максимальный FPS: {m.Max ?? "?"}");
			sb.AppendLine($"Минимальный FPS: {m.Min ?? "?"}");
			sb.AppendLine($"5-й перцентиль FPS: {m.P5 ?? "?"}");
			sb.AppendLine($"Видеопамять, ГБ: {m.Vram ?? "?"}");
			sb.AppendLine($"Скриншот результатов: {r.Screenshot}");
			if (m.Avg == null || m.P5 == null) sb.AppendLine($"Текст OCR: {m.Raw}");
			sb.AppendLine("Настройки:");
			foreach (var s in r.Profile.Settings) sb.AppendLine($"  {s.Key.Replace("UI:", "")} = {s.Value}");
		}

		Console.WriteLine("\n" + sb);
		File.WriteAllText("report.txt", sb.ToString());
	}
}