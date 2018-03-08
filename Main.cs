﻿//----------------------------------------------------------------------------
// Copyright (c) 2013 - 2014 Pope Kim (www.popekim.com)
//
// See the file LICENSE for copying permission.
//-----------------------------------------------------------------------------

//#define DO_PROFILE

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;

namespace YouThumb
{
    public partial class frmMain : Form
    {
        public frmMain()
        {
            InitializeComponent();
        }

        // from: http://stackoverflow.com/questions/3652046/c-sharp-regex-to-get-video-id-from-youtube-and-vimeo-by-url
        static private readonly Regex YoutubeVideoRegex = new Regex(@"youtu(?:\.be|be\.com)/(?:.*v(?:/|=)|(?:.*/)?)([a-zA-Z0-9-_]+)", RegexOptions.IgnoreCase);
        private string currentVideoID = "";
        private Image cachedImage;
        private string cachedTitle;

        private string getVideoID(string url)
        {
            string id = string.Empty;

            Match youtubeMatch = YoutubeVideoRegex.Match(url);
            if (youtubeMatch.Success)
            {
                id = youtubeMatch.Groups[1].Value;
            }

            return id;
        }

        private static readonly int MaxWordWrapLines = 3;
        private List<string> GetWordWrappedText(ref int fontSize, StringFormat stringFormat, RectangleF rect, Graphics graphics)
        {
            const int MinFontSize = 10;

            var lines = new List<string>(MaxWordWrapLines);

            string[] tokens = cachedTitle.Split(' ');

            var origin = new PointF(0, 0);
            bool keepTrying = false;
            var fontName = cbFonts.SelectedItem as string;
            do
            {
                keepTrying = false;
                --fontSize;

                // reytry with a smaller font size
                lines.Clear();
                lines.Add("");

                var tmpFont = new Font(fontName, fontSize, FontStyle.Bold);
                foreach (var w in tokens)
                {
                    // make sure each word is not bigger
                    var renderedSize = graphics.MeasureString(w, tmpFont, origin, stringFormat);
                    if (renderedSize.Width >= rect.Width)
                    {
                        keepTrying = true;
                        break;
                    }

                    var lineToTest = new string(lines[lines.Count - 1].ToCharArray());

                    // if it's not first word pad a space
                    if (lineToTest.Length != 0)
                    {
                        lineToTest += " ";
                    }
                    lineToTest += w;

                    renderedSize = graphics.MeasureString(lineToTest, tmpFont, origin, stringFormat);
                    if (renderedSize.Width > rect.Width)
                    {
                        if (lines.Count == MaxWordWrapLines ||                      // last line
                            rect.Height / (lines.Count + 1) < renderedSize.Height)  // make sure it fits into each line's height
                        {
                            keepTrying = true;
                            break;
                        }

                        lines.Add("");
                    }

                    if (lines[lines.Count - 1].Length != 0)
                    {
                        lines[lines.Count - 1] += " ";
                    }
                    lines[lines.Count - 1] += w;
                }
            }
            while (keepTrying && fontSize > MinFontSize);

            return lines;
        }

        private void GenerateThumb()
        {
            if (cachedImage == null)
            {
                return;
            }

            // 1) some setup
            var tmpImage = new Bitmap(cachedImage.Width, cachedImage.Height);
            var copyImage = (Image)cachedImage.Clone();

            int thumbWidth = tmpImage.Width;
            int thumbHeight = tmpImage.Height;

            const int margin = 75;

            // make left background point
            Point[] polygonPoints = new Point[4];
            polygonPoints[0] = new Point(0, 0);
            polygonPoints[1] = new Point(thumbWidth / 2 + margin, 0);
            polygonPoints[2] = new Point(thumbWidth / 2 - margin, thumbHeight);
            polygonPoints[3] = new Point(0, thumbHeight);

            using (var backgroundColor = new SolidBrush(Color.FromArgb(42, 42, 42)))
            using (var backgroundEdgeColor = new Pen(Color.FromArgb(42, 42, 42)))
            using (Graphics g = Graphics.FromImage(tmpImage))
            {
                // draw thumbnail 25% to the right
                g.DrawImage(copyImage, new Point(cachedImage.Width / 4, 0));
                // draw left background
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawPolygon(backgroundEdgeColor, polygonPoints);
                g.FillPolygon(backgroundColor, polygonPoints);
            }

            Graphics graphics = Graphics.FromImage(tmpImage);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            StringFormat stringFormat = new StringFormat();
            stringFormat.Alignment = StringAlignment.Near;
            stringFormat.LineAlignment = StringAlignment.Center;

            // 2) calculate proper draw region based on ratio
            // some youtube thumbs are shown in 4:3 ratio. so let's make it 4:3 ratio
            float marginPercentW = 0;
            if (tmpImage.Height * 16 == tmpImage.Width * 9)
            {
                marginPercentW = 4 * 0.5F / 16F;
            }

            var rect = new RectangleF(margin,
                margin * 3,
                thumbWidth / 2 - margin * 3,
                thumbHeight - margin * 6);

            // 3) properly word wrap. (.NET function text-wraps at character level, but we want word-level wrap
            // find some biggest fontsize we will begin with. GetWordWrappedText will find the proper smaller font size
            // that makes everything fit into the draw region
            var fontSize = Math.Min(thumbWidth, thumbHeight) / 2;
#if DO_PROFILE
            Stopwatch profiler = Stopwatch.StartNew();
#endif
            var lines = GetWordWrappedText(ref fontSize, stringFormat, rect, graphics);
#if DO_PROFILE
            profiler.Stop();
            Console.WriteLine(String.Format("GetWorldWrappedText() took {0} ms", profiler.ElapsedMilliseconds));
#endif
            // 4) divide draw rect into N regions and draw each line.
#if DO_PROFILE
            profiler = Stopwatch.StartNew();
#endif
            var font = new Font(cbFonts.SelectedItem as string, fontSize, FontStyle.Bold);

            var numLines = lines.Count;
            float heightPerRow = rect.Height / numLines;
            rect.Height = heightPerRow;

            for (int i = 0; i < numLines; ++i)
            {
                graphics.DrawString(lines[i], font, Brushes.White, rect, stringFormat);
                rect.Y += heightPerRow;
            }
#if DO_PROFILE
            profiler.Stop();
            Console.WriteLine(String.Format("RenderFont() took {0} ms", profiler.ElapsedMilliseconds));
#endif
            // 5) finally set the image to the picture box
            pbThumb.Image = tmpImage;
        }

        // always retrieves best definition image
        private bool retrieveYoutubeVideoData(string videoID)
        {
            if (videoID == currentVideoID)
            {
                return false;
            }

            string[] modesToTry = { "maxresdefault", "mqdefault" };

            Image image = null;

            foreach (var mode in modesToTry)
            {
                var thumbURL = $"https://img.youtube.com/vi/{videoID}/{mode}.jpg";

                try
                {
                    image = Image.FromStream(new MemoryStream(new WebClient().DownloadData(thumbURL)));
                }
                catch (Exception)
                {
                    // TODO: show error
                }

                if (image != null)
                {
                    break;
                }
            }

            if (image == null)
            {
                return false;
            }


            string title = null;
            try
            {
                // HACK: read whole html page and parse title. It's to avoid oAuth request to call google api :(
                var videoPageUrl = new Uri(String.Format(@"https://www.youtube.com/watch?v={0}", videoID));

                using (var webclient = new WebClient())
                using (var memoryStream = new MemoryStream(webclient.DownloadData(videoPageUrl)))
                using (var streamReader = new StreamReader(memoryStream))
                {
                    var contents = streamReader.ReadToEnd();

                    const string beginToken = "<title>";
                    const string endToken = " - YouTube</title>";
                    var startIndex = contents.IndexOf(beginToken) + beginToken.Length;
                    var endIndex = contents.IndexOf(endToken);

                    title = contents.Substring(startIndex, endIndex - startIndex);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                // TODO: show error
            }

            currentVideoID = videoID;
            if (image.Height < 1080)
            {
                var scale = 1080.0 / image.Height;
                var w = (int)(image.Width * scale);
                var h = (int)(image.Height * scale);
                image = ResizeImage(image, w, h);
            }
            cachedImage = image;
            cachedTitle = title;

            return true;

        }
        public static Bitmap ResizeImage(Image image, int width, int height)
        {
            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }

        private void tbURL_TextChanged(object sender, EventArgs e)
        {
            var videoID = getVideoID(tbURL.Text);
            if (videoID == string.Empty)
            {
                return;
            }

            // got valid youtube id - so let's load youtube image
            var shouldGenerateNewThumb = retrieveYoutubeVideoData(videoID);

            if (shouldGenerateNewThumb)
            {
                GenerateThumb();
            }
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            var fontNameList = new List<string>(System.Drawing.FontFamily.Families.Length);
            foreach (var f in System.Drawing.FontFamily.Families)
            {
                fontNameList.Add(f.Name);
            }
            cbFonts.Items.AddRange(fontNameList.ToArray());

            var font = Properties.Settings.Default["fontname"];
            int fontIndex = -1;
            if (font != null)
            {
                fontIndex = fontNameList.FindIndex(f => f == font.ToString());
            }

            if (fontIndex < 0)
            {
                fontIndex = fontNameList.FindIndex(f => f == "Verdana");
            }

            cbFonts.SelectedIndex = (fontIndex >= 0) ? fontIndex : 0;
        }

        private void cbFonts_SelectedIndexChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default["fontname"] = cbFonts.SelectedItem as string;
            GenerateThumb();
        }

        private void pbThumb_Click(object sender, EventArgs e)
        {
            if (pbThumb.Image == null)
            {
                return;
            }

            if (dlgSave.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    pbThumb.Image.Save(dlgSave.FileName);
                }
                catch (Exception)
                {
                    MessageBox.Show("failed to save " + dlgSave.FileName, "FAIL", MessageBoxButtons.OK);
                }
            }
        }

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            Properties.Settings.Default.Save();
        }
    }
}
