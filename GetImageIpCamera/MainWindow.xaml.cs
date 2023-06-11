using PluginBase;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GetImageIpCamera
{
    public partial class MainWindow : Window
    {
        public delegate void NextPrimeDelegate();

        private static Mutex mut = new();

        private bool continueGetting = false;

        private double contrastValue = 50.0;

        private double gammaValue = 1.2;

        private string imgPath = string.Empty;

        // Интерфейсы перечисления для подключаемых команд модулей
        IEnumerable<ICommand>? ContrastExtensionCommands;

        IEnumerable<ICommand>? GammaExtensionCommands;

        public MainWindow()
        {
            InitializeComponent();

            LoadPlugins();
        }

        private void StGetImgButton_Click(object sender, RoutedEventArgs e)
        {

            if (continueGetting)
            {
                continueGetting = false;
                stGetImgButton.Content = "Продолжить получать изображения с IP-камеры";
            }
            else
            {
                continueGetting = true;
                stGetImgButton.Content = "Остановить получение изображений с IP-камеры";

                string[] urls = new string[] { "https://fl-0.telecoma.tv/Glazok_1080_Marksa_127_2/preview.jpg",
        "https://fl-0.telecoma.tv/Glazok_1080_Dekabristov_4_1/preview.jpg"};

                // Create the threads that will use the protected resource.
                for (int i = 0; i < int.Parse(threadsCount.Text); i++)
                {
                    if (i > urls.Length - 1) { break; }

                    string t = urls[i];

                    Thread newThread = new(x => GetNewImage(t))
                    {
                        IsBackground = true,
                        Name = string.Format("Thread{0}", i + 1)
                    };
                    newThread.Start();
                }

                Thread logThread = new(x => GetLog())
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest
                };
                logThread.Start();

            }
        }

        private void GetLog()
        {
            Thread.Sleep(5000);

            GetInfoAboutThreads_Click(this, new RoutedEventArgs());

            GetLog();
        }

        private async void GetNewImage(string url)
        {
            using (var handler = new HttpClientHandler())
            {
                handler.UseDefaultCredentials = true;

                using (var client = new HttpClient(handler))
                {
                    client.BaseAddress = new Uri(url);

                    HttpResponseMessage httpResponseMessage = await client.GetAsync(url);

                    BitmapImage bitmap = new();

                    if (httpResponseMessage != null && httpResponseMessage.StatusCode == HttpStatusCode.OK)
                    {
                        using (var stream = await httpResponseMessage.Content.ReadAsStreamAsync())
                        {
                            using (var memStream = new MemoryStream())
                            {
                                await stream.CopyToAsync(memStream);
                                memStream.Position = 0;

                                bitmap.StreamSource = memStream;
                            }
                        }
                    }

                    httpResponseMessage.EnsureSuccessStatusCode();

                    Random rnd = new();
                    int num = rnd.Next();

                    var streamGot = await client.GetStreamAsync(url);
                    await using var fileStream = new FileStream(AppDomain.CurrentDomain.BaseDirectory + "/bmpImage" + num + ".jpeg", FileMode.Create, FileAccess.Write);
                    streamGot.CopyTo(fileStream);

                    fileStream.Close();

                    var resultBitmapImage = ToBitmapImage(GammaExtensionCommands.First().Execute(ContrastExtensionCommands.First().Execute(BitmapImage2Bitmap(new BitmapImage(
                        new Uri(AppDomain.CurrentDomain.BaseDirectory + "/bmpImage" + num + ".jpeg"))), contrastValue), gammaValue));


                    Dispatcher.Invoke(() =>
                    {
                        if (url.Contains("Glazok_1080_Marksa_127_2"))
                            outStreamImage.Source = resultBitmapImage;
                        else if (url.Contains("Glazok_1080_Dekabristov_4_1"))
                            outStreamImage2.Source = resultBitmapImage;
                    });

                    if (imgPath != string.Empty)
                    {
                        File.Delete(imgPath);

                        imgPath = AppDomain.CurrentDomain.BaseDirectory + "/bmpImage" + num + ".jpeg";
                    }
                    else
                    {
                        imgPath = AppDomain.CurrentDomain.BaseDirectory + "/bmpImage" + num + ".jpeg";
                    }

                    mut.WaitOne();

                    AddDataToDB("/bmpImage" + num + ".jpeg");

                    mut.ReleaseMutex();
                }
            }

            if (continueGetting)
            {

                GetNewImage(url);
            }
        }

        private void AddDataToDB(string info)
        {
            using (SqlConnection connection = new SqlConnection(Properties.Settings.Default.connString))
            {
                string queryString = "INSERT INTO LoggerTable ([Time], [Thread number], [Info]) VALUES ('" + DateTime.Now.ToString("yyyyMMdd") + "', '" +
                    Environment.CurrentManagedThreadId.ToString() + "', '" + info + "');";

                // Create a SqlCommand, and identify it as a stored procedure
                using (SqlCommand sqlCommand = new SqlCommand(queryString, connection))
                {
                    connection.Open();

                    using SqlDataReader reader = sqlCommand.ExecuteReader();
                }
            }
        }

        private static Bitmap BitmapImage2Bitmap(BitmapImage bitmapImage)
        {
            using (MemoryStream outStream = new())
            {
                BitmapEncoder enc = new BmpBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bitmapImage));
                enc.Save(outStream);
                Bitmap bitmap = new(outStream);

                return new Bitmap(bitmap);
            }
        }

        public static BitmapImage ToBitmapImage(Bitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                memory.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
        }

        private void LoadPlugins()
        {
            string[] pluginsPathes = new string[]
                {
                    AppDomain.CurrentDomain.BaseDirectory + "\\ContrastExtensionPlugin.dll",
                    AppDomain.CurrentDomain.BaseDirectory + "\\GammaExtensionPlugin.dll"
                };

            for (int i = 0; i < pluginsPathes.Length; i++)
            {
                Assembly pluginAssembly = LoadPlugin(pluginsPathes[i]);

                IEnumerable<ICommand> commands = CreateCommands(pluginAssembly);

                if (commands.First().Name == "ContrastExtension")
                {
                    ContrastExtensionCommands = commands;
                }
                else if (commands.First().Name == "GammaExtension")
                {
                    GammaExtensionCommands = commands;
                }
            }
        }

        // Статический метод подключения плагина
        // string relativePath - путь к подключаемому плагину
        static Assembly LoadPlugin(string relativePath)
        {
            // Navigate up to the solution root
            string root = Path.GetFullPath(typeof(MainWindow).Assembly.Location);

            string pluginLocation = Path.GetFullPath(Path.Combine(root, relativePath.Replace('\\', Path.DirectorySeparatorChar)));

            PluginLoadContext loadContext = new(pluginLocation);
            return loadContext.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(pluginLocation)));
        }

        // Статический метод создания команд для подключенного плагина
        // Assembly assembly - блок (библиотека runtime) подключенный к приложению
        static IEnumerable<ICommand> CreateCommands(Assembly assembly)
        {
            int count = 0;

            foreach (Type type in assembly.GetTypes())
            {
                if (typeof(ICommand).IsAssignableFrom(type))
                {
                    if (Activator.CreateInstance(type) is ICommand result)
                    {
                        count++;
                        yield return result;
                    }
                }
            }

            if (count == 0)
            {
                string availableTypes = string.Join(",", assembly.GetTypes().Select(t => t.FullName));
                throw new ApplicationException(
                    $"Can't find any type which implements ICommand in {assembly} from {assembly.Location}.\n" +
                    $"Available types: {availableTypes}");
            }
        }

        private void ThreadsCount_TextInput(object sender, TextChangedEventArgs e)
        {
            try
            {
                int t = int.Parse(threadsCount.Text);

                if (t > 0 && t < int.MaxValue)
                {
                    //ThreadPool.SetMaxThreads(t, t);
                }
                else
                {
                    //ThreadPool.GetMaxThreads(out t, out t);
                    //threadsCount.Text = t.ToString();
                    threadsCount.Text = "4";
                }
            }
            catch { }
        }

        private void ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            contrastValue = ((Slider)sender).Value;
        }

        private void GammaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            gammaValue = ((Slider)sender).Value;
        }

        private void GetInfoAboutThreads_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button || continueGetting == true)
                using (SqlConnection connection = new SqlConnection(Properties.Settings.Default.connString))
                {
                    string queryString = "SELECT [Thread number], COUNT ([Thread number]) FROM LoggerTable GROUP BY [Thread number]";

                    using (SqlCommand command = new SqlCommand(queryString, connection))
                    {
                        connection.Open();

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                ReadSingleRow(reader);
                            }
                        }
                    };
                }

            if (sender is Button)
            {
                Process.Start("notepad.exe", AppDomain.CurrentDomain.BaseDirectory + "/log.txt");
            }
        }

        private static void ReadSingleRow(IDataRecord dataRecord)
        {
            using (StreamWriter outputFile = new StreamWriter(AppDomain.CurrentDomain.BaseDirectory + "/log.txt", true))
            {
                outputFile.WriteLine(string.Format("{0}, {1}", dataRecord[0], dataRecord[1]));

                outputFile.Close();
            }
        }
    }
}
