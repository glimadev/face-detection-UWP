using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;
// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace FaceDetectionApp
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly SolidColorBrush lineBrush = new SolidColorBrush(Windows.UI.Colors.Yellow);
        private readonly double lineThickness = 3.0;
        private readonly SolidColorBrush fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);

        MediaCapture mediaCapture;
        string serviceNameForConnect = "22112";
        string hostNameForConnect = "localhost";
        StreamSocket clientSocket = null;
        FaceDetector faceDetector;
        IList<DetectedFace> detectedFaces;

        public MainPage()
        {
            this.InitializeComponent();
        }

        private async void StartListener_Click(object sender, RoutedEventArgs e)
        {
            StreamSocketListener listener = new StreamSocketListener();

            listener.ConnectionReceived += OnConnection;

            await listener.BindServiceNameAsync(serviceNameForConnect);
        }

        private async void ConnectSocket_Click(object sender, RoutedEventArgs e)
        {
            HostName hostName;

            mediaCapture = new MediaCapture();

            await mediaCapture.InitializeAsync();

            try
            {
                hostName = new HostName(hostNameForConnect);
            }
            catch (ArgumentException ex)
            {
                return;
            }

            clientSocket = new StreamSocket();

            try
            {
                await clientSocket.ConnectAsync(hostName, serviceNameForConnect);
            }
            catch (Exception exception)
            {
                // If this is an unknown status it means that the error is fatal and retry will likely fail.
                if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                {
                    throw;
                }
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            object outValue;
            // Create a DataWriter if we did not create one yet. Otherwise use one that is already cached.
            DataWriter writer;

            if (!CoreApplication.Properties.TryGetValue("clientDataWriter", out outValue))
            {
                writer = new DataWriter(clientSocket.OutputStream);

                CoreApplication.Properties.Add("clientDataWriter", writer);
            }
            else
            {
                writer = (DataWriter)outValue;
            }           

            while (true)
            {
                var memoryStream = new InMemoryRandomAccessStream();               

                await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), memoryStream);                

                memoryStream.Seek(0);

                writer.WriteUInt32((uint)memoryStream.Size);

                writer.WriteBuffer(await memoryStream.ReadAsync(new byte[memoryStream.Size].AsBuffer(), (uint)memoryStream.Size, InputStreamOptions.None));

                // Write the locally buffered data to the network.
                try
                {
                    await writer.StoreAsync();
                }
                catch (Exception exception)
                {
                    // If this is an unknown status it means that the error if fatal and retry will likely fail.
                    if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                    {
                        throw;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(18.288)); //60 fps
            }
        }

        private async void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            await Task.WhenAll(DownloadVideos(args));
        }

        public async Task DownloadVideos(StreamSocketListenerConnectionReceivedEventArgs args)
        {
            DataReader reader = new DataReader(args.Socket.InputStream);

            try
            {
                while (true)
                {
                    // Read first 4 bytes (length of the subsequent string).
                    uint sizeFieldCount = await reader.LoadAsync(sizeof(uint));

                    if (sizeFieldCount != sizeof(uint))
                    {
                        // The underlying socket was closed before we were able to read the whole data.
                        return;
                    }
                    uint stringLength = reader.ReadUInt32();

                    uint actualStringLength = await reader.LoadAsync(stringLength);

                    if (stringLength != actualStringLength)
                    {
                        // The underlying socket was closed before we were able to read the whole data.
                        return;
                    }

                    NotifyUserFromAsyncThread(reader.ReadBuffer(actualStringLength));
                }
            }
            catch (Exception exception)
            {
                // If this is an unknown status it means that the error is fatal and retry will likely fail.
                if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                {
                    throw;
                }
            }
        }

        private void NotifyUserFromAsyncThread(IBuffer buffer)
        {
            var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    GetFaces(buffer);                    
                });
        }

        private async void GetFaces(IBuffer ms)
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(ms.AsStream().AsRandomAccessStream());

            BitmapTransform transform = new BitmapTransform();

            const float sourceImageHeightLimit = 1280;

            //diminui o tamanho da foto pq o algoritimo é mais rapido para fotos menores

            if (decoder.PixelHeight > sourceImageHeightLimit)
            {
                float scalingFactor = (float)sourceImageHeightLimit / (float)decoder.PixelHeight;
                transform.ScaledWidth = (uint)Math.Floor(decoder.PixelWidth * scalingFactor);
                transform.ScaledHeight = (uint)Math.Floor(decoder.PixelHeight * scalingFactor);
            }
            else
            {
                transform.ScaledWidth = decoder.PixelWidth;
                transform.ScaledHeight = decoder.PixelHeight;
            }

            SoftwareBitmap sourceBitmap = await decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

            // Use FaceDetector.GetSupportedBitmapPixelFormats and IsBitmapPixelFormatSupported to dynamically
            // determine supported formats
            const BitmapPixelFormat faceDetectionPixelFormat = BitmapPixelFormat.Gray8;

            SoftwareBitmap convertedBitmap;

            if (sourceBitmap.BitmapPixelFormat != faceDetectionPixelFormat)
            {
                convertedBitmap = SoftwareBitmap.Convert(sourceBitmap, faceDetectionPixelFormat);
            }
            else
            {
                convertedBitmap = sourceBitmap;
            }

            if (faceDetector == null)
            {
                faceDetector = await FaceDetector.CreateAsync();
            }

            detectedFaces = await faceDetector.DetectFacesAsync(convertedBitmap);

            await ShowDetectedFaces(sourceBitmap, detectedFaces);

            sourceBitmap.Dispose();
            convertedBitmap.Dispose();
        }

        private async Task ShowDetectedFaces(SoftwareBitmap sourceBitmap, IList<DetectedFace> faces)
        {
            ImageBrush brush = new ImageBrush();
            SoftwareBitmapSource bitmapSource = new SoftwareBitmapSource();
            await bitmapSource.SetBitmapAsync(sourceBitmap);
            brush.ImageSource = bitmapSource;
            brush.Stretch = Stretch.Fill;
            this.VisualizationCanvas.Background = brush;

            if (detectedFaces != null)
            {
                this.VisualizationCanvas.Children.Clear();

                double widthScale = sourceBitmap.PixelWidth / this.VisualizationCanvas.ActualWidth;
                double heightScale = sourceBitmap.PixelHeight / this.VisualizationCanvas.ActualHeight;

                foreach (DetectedFace face in detectedFaces)
                {
                    // Create a rectangle element for displaying the face box but since we're using a Canvas
                    // we must scale the rectangles according to the image’s actual size.
                    // The original FaceBox values are saved in the Rectangle's Tag field so we can update the
                    // boxes when the Canvas is resized.
                    Rectangle box = new Rectangle();
                    box.Tag = face.FaceBox;
                    box.Width = (uint)(face.FaceBox.Width / widthScale);
                    box.Height = (uint)(face.FaceBox.Height / heightScale);
                    box.Fill = this.fillBrush;
                    box.Stroke = this.lineBrush;
                    box.StrokeThickness = this.lineThickness;
                    box.Margin = new Thickness((uint)(face.FaceBox.X / widthScale), (uint)(face.FaceBox.Y / heightScale), 0, 0);

                    this.VisualizationCanvas.Children.Add(box);                    
                }
            }
        }
    }
}
