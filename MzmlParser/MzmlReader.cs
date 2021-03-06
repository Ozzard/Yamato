using NLog;
using System;
using System.Xml;
using System.Linq;
using System.Globalization;
using System.Threading;
using Ionic.Zlib;
using System.IO;
using System.Collections.Generic;
using LibraryParser;

namespace MzmlParser
{

    public class MzmlReader
    {
        public MzmlReader()
        {
            ParseBinaryData = true;
            ExtractBasePeaks = true;
            Threading = true;
        }

        public bool ExtractBasePeaks { get; set; }
        public bool ParseBinaryData { get; set; }
        public bool Threading { get; set; }

        private const double BasePeakMinimumIntensity = 100;
        public int currentCycle = 0;
        bool MS1 = false;
        public double previousTargetMz = 0;

        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static CountdownEvent cde = new CountdownEvent(1);
        private static readonly Object Lock = new Object();
        private string SurveyScanReferenceableParamGroupId; //This is the referenceableparamgroupid for the survey scan

        public Run LoadMzml(string path, bool storeScansInMemory, AnalysisSettings analysisSettings)
        {
            Run run = new Run() { AnalysisSettings = analysisSettings };
            bool irt = run.AnalysisSettings.IrtLibrary != null;
            run.MissingScans = 0;
            run.StartTime = 100;
            run.LastScanTime = 0;

            ReadMzml(path, run, irt);

            cde.Signal();
            cde.Wait();
            cde.Reset(1);

            AddBasePeakSpectra(run);

            cde.Signal();
            cde.Wait();
            cde.Reset(1);
            IrtPeptideMatcher.ChooseIrtPeptides(run);

            return run;
        }

        private void ReadMzml(string path, Run run, bool irt)
        {
            using (XmlReader reader = XmlReader.Create(path))
            {
                while (reader.Read())
                {
                    if (reader.IsStartElement())
                    {
                        switch (reader.LocalName)
                        {
                            case "sourceFile":
                                ReadSourceFileMetaData(reader, run);
                                break;
                            case "spectrum":
                                ReadSpectrum(reader, run, irt);
                                break;
                            case "referenceableParamGroup":
                                if (String.IsNullOrEmpty(SurveyScanReferenceableParamGroupId))
                                    SurveyScanReferenceableParamGroupId = reader.GetAttribute("id");
                                break;
                        }
                    }
                }
            }
        }


        public void AddInfoToBasePeaks(Run run, QuickScan qs)
        {
            float[] intensities = ExtractFloatArray(qs.Base64IntensityArray, qs.IntensityZlibCompressed, qs.IntensityBitLength);
            float[] mzs = ExtractFloatArray(qs.Base64MzArray, qs.MzZlibCompressed, qs.MzBitLength);


        }

        public void ReadSourceFileMetaData(XmlReader reader, Run run)
        {
            bool cvParamsRead = false;
            run.SourceFileName = Path.GetFileName(reader.GetAttribute("name"));
            run.SourceFileType = Path.GetExtension(reader.GetAttribute("name"));
            run.SourceFilePath = reader.GetAttribute("location");

            while (reader.Read() && !cvParamsRead)
            {
                if (reader.IsStartElement())
                {
                    if (reader.LocalName == "cvParam")
                    {
                        switch (reader.GetAttribute("accession"))
                        {
                            case "MS:1000569":
                                run.SourceFileChecksum = reader.GetAttribute("value");
                                run.FilePropertiesAccession = "MS:1000569";
                                break;
                            case "MS:1000747"://Optional for the conversion process, but included in mzQC
                                run.CompletionTime = reader.GetAttribute("value");
                                break;
                        }
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "sourceFile")
                {
                    cvParamsRead = true;
                }
            }
        }

        public void ReadSpectrum(XmlReader reader, Run run, bool irt)
        {
            ScanAndTempProperties scan = new ScanAndTempProperties();

            //The cycle number is within a kvp string in the following format: "sample=1 period=1 cycle=1 experiment=1"
            //
            //This is a bit code-soup but I didn't want to spend more than one line on it and it should be robust enough not just to select on index
            //
            //This has only been tested on Sciex converted data
            //
            //Paul Brack 2019/04/03

            bool CycleInfoInID = false;

            if (run.SourceFileType.EndsWith("wiff", StringComparison.InvariantCultureIgnoreCase) || run.SourceFileType.ToUpper().EndsWith("scan", StringComparison.InvariantCultureIgnoreCase))
            {
                scan.Scan.Cycle = int.Parse(reader.GetAttribute("id").Split(' ').DefaultIfEmpty("0").Single(x => x.Contains("cycle")).Split('=').Last());
                if (scan.Scan.Cycle != 0)//Some wiffs don't have that info so let's check
                {
                    CycleInfoInID = true;
                }
            }

            bool cvParamsRead = false;
            while (reader.Read() && !cvParamsRead)
            {
                if (reader.IsStartElement())
                {
                    if (reader.LocalName == "cvParam")
                    {
                        switch (reader.GetAttribute("accession"))
                        {

                            case "MS:1000511":
                                scan.Scan.MsLevel = int.Parse(reader.GetAttribute("value"));
                                break;
                            case "MS:1000285":
                                scan.Scan.TotalIonCurrent = double.Parse(reader.GetAttribute("value"), CultureInfo.InvariantCulture);
                                break;
                            case "MS:1000016":
                                scan.Scan.ScanStartTime = double.Parse(reader.GetAttribute("value"), CultureInfo.InvariantCulture);
                                run.StartTime = Math.Min(run.StartTime, scan.Scan.ScanStartTime);
                                run.LastScanTime = Math.Max(run.LastScanTime, scan.Scan.ScanStartTime);//technically this is the starttime of the last scan not the completion time
                                break;
                            
                            case "MS:1000829":
                                scan.Scan.IsolationWindowUpperOffset = double.Parse(reader.GetAttribute("value"), CultureInfo.InvariantCulture);
                                break;
                            case "MS:1000828":
                                scan.Scan.IsolationWindowLowerOffset = double.Parse(reader.GetAttribute("value"), CultureInfo.InvariantCulture);
                                break;
                            case "MS:1000827":
                                scan.Scan.IsolationWindowTargetMz = double.Parse(reader.GetAttribute("value"), CultureInfo.InvariantCulture);
                                break;

                        }
                    }
                    else if (reader.LocalName == "binaryDataArray")
                    {
                        GetBinaryData(reader, scan);
                    }
                    if (scan.Scan.MsLevel == null && reader.LocalName == "referenceableParamGroupRef")
                    {
                        if (reader.GetAttribute("ref") == SurveyScanReferenceableParamGroupId)
                            scan.Scan.MsLevel = 1;
                        else
                            scan.Scan.MsLevel = 2;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "spectrum")
                {

                    if (!CycleInfoInID)
                    {
                        if (scan.Scan.MsLevel == 1)
                        {
                            currentCycle++;
                            scan.Scan.Cycle = currentCycle;
                            MS1 = true;
                        }
                        //if there is ScanAndTempProperties ms1:
                        else if (MS1)
                        {
                            scan.Scan.Cycle = currentCycle;
                        }
                        //if there is no ms1:
                        else
                        {
                            if (previousTargetMz < scan.Scan.IsolationWindowTargetMz)
                            {
                                scan.Scan.Cycle = currentCycle;
                            }
                            else
                            {
                                currentCycle ++;
                                scan.Scan.Cycle = currentCycle;
                            }
                        }
                    }

                    previousTargetMz = scan.Scan.IsolationWindowTargetMz;

                    if (ParseBinaryData)
                    {
                        if (Threading)
                        {
                            cde.AddCount();
                            ThreadPool.QueueUserWorkItem(state => ParseBase64Data(scan, run, ExtractBasePeaks, Threading, irt));
                        }
                        else
                        {
                            ParseBase64Data(scan, run, ExtractBasePeaks, Threading, irt);
                        }
                    }
                    else
                    {
                        AddScanToRun(scan.Scan, run);
                    }
                    cvParamsRead = true;

                }
            }
        }

        private static void GetBinaryData(XmlReader reader, ScanAndTempProperties scan)
        {
            string base64 = String.Empty;
            int bits = 0;
            bool intensityArray = false;
            bool mzArray = false;
            bool binaryDataArrayRead = false;
            bool IsZlibCompressed = false;

            while (reader.Read() && binaryDataArrayRead == false)
            {
                if (reader.IsStartElement())
                {
                    if (reader.LocalName == "cvParam")
                    {
                        switch (reader.GetAttribute("accession"))
                        {
                            case "MS:1000574":
                                if (reader.GetAttribute("name") == "zlib compression")
                                    IsZlibCompressed = true;
                                break;
                            case "MS:1000521":
                                //32 bit float
                                bits = 32;
                                break;
                            case "MS:1000523":
                                //64 bit float
                                bits = 64;
                                break;
                            case "MS:1000515":
                                //intensity array
                                intensityArray = true;
                                break;
                            case "MS:1000514":
                                //mz array
                                mzArray = true;
                                break;
                        }
                    }
                    else if (reader.LocalName == "binary")
                    {
                        base64 = reader.ReadElementContentAsString();
                    }

                }
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "binaryDataArray")
                {
                    binaryDataArrayRead = true;
                    if (intensityArray)
                    {
                        scan.Base64IntensityArray = base64;
                        scan.IntensityBitLength = bits;
                        scan.IntensityZlibCompressed = IsZlibCompressed;
                    }
                    else if (mzArray)
                    {
                        scan.Base64MzArray = base64;
                        scan.MzBitLength = bits;
                        scan.MzZlibCompressed = IsZlibCompressed;
                    }
                }
            }
        }

        private static void ParseBase64Data(ScanAndTempProperties scan, Run run, bool extractBasePeaks, bool threading, bool irt)
        {

            float[] intensities = ExtractFloatArray(scan.Base64IntensityArray, scan.IntensityZlibCompressed, scan.IntensityBitLength);
            float[] mzs = ExtractFloatArray(scan.Base64MzArray, scan.MzZlibCompressed, scan.MzBitLength);

            if (intensities.Count() == 0)
            {
                intensities = FillZeroArray(intensities);
                mzs = FillZeroArray(mzs);
                logger.Info("Empty binary array for a MS{0} scan in cycle number: {0}. The empty scans have been filled with zero values.", scan.Scan.MsLevel, scan.Scan.Cycle);
                run.MissingScans++;
            }
            var spectrum = intensities.Select((x, i) => new SpectrumPoint() { Intensity = x, Mz = mzs[i], RetentionTime = (float)scan.Scan.ScanStartTime }).ToList();


            //Predicted singly charged proportion:

            //The theory is that an M and M+1 pair are singly charged so we are very simply just looking for  occurences where two ions are 1 mz apart (+-massTolerance)

            //We therefore create an array cusums that accumulates the difference between ions, so for every ion we calculate the distance between that ion
            //and the previous and add that to each of the previous ions' cusum of differences. If the cusum of an ion overshoots 1 +massTolerance, we stop adding to it, if it reaches our mark we count it and stop adding to it

            List<int> indexes = new List<int>();
            float[] cusums = new float[mzs.Length];
            int movingPoint = 0;
            double minimum = 1 - 0.001;
            double maximum = 1 + 0.001;

            for (int i = 1; i < mzs.Length; i++)
            {
                float distance = mzs[i] - mzs[i - 1];
                bool matchedWithLower = false;
                for (int ii = movingPoint; ii < i; ii++)
                {
                    cusums[ii] += distance;
                    if (cusums[ii] < minimum) continue;
                    else if (cusums[ii] > minimum && cusums[ii] < maximum)
                    {
                        if (!matchedWithLower)//This is to try and minimise false positives where for example if you have an array: 351.14, 351.15, 352.14 all three get chosen.
                        {
                            indexes.Add(i);
                            indexes.Add(movingPoint);
                        }
                        movingPoint +=1;
                        matchedWithLower = true;
                        continue;
                    }
                    else if (cusums[ii] > maximum) { movingPoint += 1; }
                }

            }
            int distinct = indexes.Distinct().Count();
            int len = mzs.Length;
            scan.Scan.proportionChargeStateOne = (double)distinct/(double)len;

            
            scan.Scan.Spectrum = spectrum;
            scan.Scan.IsolationWindowLowerBoundary = scan.Scan.IsolationWindowTargetMz - scan.Scan.IsolationWindowLowerOffset;
            scan.Scan.IsolationWindowUpperBoundary = scan.Scan.IsolationWindowTargetMz + scan.Scan.IsolationWindowUpperOffset;

            scan.Scan.Density = spectrum.Count();
            scan.Scan.BasePeakIntensity = intensities.Max();
            scan.Scan.BasePeakMz = mzs[Array.IndexOf(intensities, intensities.Max())];
            AddScanToRun(scan.Scan, run);
            float basepeakIntensity;
            if (intensities.Count() > 0)
            {
                basepeakIntensity = intensities.Max();
                int maxIndex = intensities.ToList().IndexOf(basepeakIntensity);
                double mz = mzs[maxIndex];

                if (run.BasePeaks.Count(x => Math.Abs(x.Mz - mz) < run.AnalysisSettings.MassTolerance) < 1)//If a basepeak with this mz doesn't exist yet add it
                {
                    BasePeak bp = new BasePeak(mz, scan.Scan.ScanStartTime, basepeakIntensity);
                    run.BasePeaks.Add(bp);
                }
                else //we do have a match, now lets figure out if they fall within the rtTolerance
                {
                    //find out which basepeak
                    foreach (BasePeak thisbp in run.BasePeaks.Where(x => Math.Abs(x.Mz - mz) < run.AnalysisSettings.MassTolerance))
                    {
                        bool found = false;
                        for (int rt = 0; rt < thisbp.BpkRTs.Count(); rt++)
                        {
                            if (Math.Abs(thisbp.BpkRTs[rt] - scan.Scan.ScanStartTime) < run.AnalysisSettings.RtTolerance)//this is part of a previous basepeak, or at least considered to be 
                            {
                                found = true;
                                break;
                            }

                        }
                        if (!found)//This is considered to be a new instance
                        {
                            thisbp.BpkRTs.Add(scan.Scan.ScanStartTime);
                            thisbp.Intensities.Add(basepeakIntensity);
                        }
                    }
                }
            }
            else { basepeakIntensity = 0; }
            //Extract info for Basepeak chromatograms

            if (irt)
                FindIrtPeptideCandidates(scan, run, spectrum);

            if (threading)
                cde.Signal();
        }

        private static void FindIrtPeptideCandidates(ScanAndTempProperties scan, Run run, List<SpectrumPoint> spectrum)
        {
            foreach (Library.Peptide peptide in run.AnalysisSettings.IrtLibrary.PeptideList.Values)
            {
                var irtIntensities = new List<float>();
                var peptideTransitions = run.AnalysisSettings.IrtLibrary.TransitionList.Values.OfType<Library.Transition>().Where(x => x.PeptideId == peptide.Id);
                int transitionsLeftToSearch = peptideTransitions.Count();
                foreach (Library.Transition t in peptideTransitions)
                {
                    if (irtIntensities.Count() + transitionsLeftToSearch < run.AnalysisSettings.IrtMinPeptides)
                    {
                        break;
                    }
                    var spectrumPoints = spectrum.Where(x => x.Intensity > run.AnalysisSettings.IrtMinIntensity && Math.Abs(x.Mz - t.ProductMz) < run.AnalysisSettings.IrtMassTolerance);
                    if (spectrumPoints.Any())
                        irtIntensities.Add(spectrumPoints.Max(x => x.Intensity));
                    transitionsLeftToSearch--;

                }
                if (irtIntensities.Count >= run.AnalysisSettings.IrtMinPeptides)
                {
                    run.IRTHits.Add(new CandidateHit()
                    {
                        PeptideSequence = peptide.Sequence,
                        Intensities = irtIntensities,
                        RetentionTime = scan.Scan.ScanStartTime,
                        PrecursorTargetMz = peptide.AssociatedTransitions.First().PrecursorMz,
                        ProductTargetMzs = peptide.AssociatedTransitions.Select(x => x.ProductMz).ToList()
                    });
                }
            }
        }

        private static void AddBasePeakSpectra(Run run)
        {
            foreach (Scan scan in run.Ms2Scans)
            {
                cde.AddCount();
                ThreadPool.QueueUserWorkItem(state => FindBasePeaks(run, scan));
            }
        }

        private static void FindBasePeaks(Run run, Scan scan)
        {
            foreach (BasePeak bp in run.BasePeaks.Where(x => Math.Abs(x.Mz - scan.BasePeakMz) <= run.AnalysisSettings.MassTolerance))
            {
                var temp = bp.BpkRTs.Where(x => Math.Abs(x - scan.ScanStartTime) < run.AnalysisSettings.RtTolerance);
                if (temp.Any())
                    bp.Spectrum.Add(scan.Spectrum.Where(x => Math.Abs(x.Mz - bp.Mz) <= run.AnalysisSettings.MassTolerance).OrderByDescending(x => x.Intensity).First());
            }
            cde.Signal();
        }

        private static float[] ExtractFloatArray(string Base64Array, bool IsZlibCompressed, int bits)
        {
            float[] floats = new float[0];
            byte[] bytes = Convert.FromBase64String(Base64Array);

            if (IsZlibCompressed)
                bytes = ZlibStream.UncompressBuffer(bytes);

            if (bits == 32)
                floats = GetFloats(bytes);
            else if (bits == 64)
                floats = GetFloatsFromDoubles(bytes);
            else
                throw new ArgumentOutOfRangeException("bits", "Numbers must be 32 or 64 bits");
            return floats;
        }

        private static float[] GetFloats(byte[] bytes)
        {
            float[] floats = new float[bytes.Length / 4];
            for (int i = 0; i < floats.Length; i++)
                floats[i] = BitConverter.ToSingle(bytes, i * 4);
            return floats;
        }

        private static float[] GetFloatsFromDoubles(byte[] bytes)
        {
            float[] floats = new float[bytes.Length / 8];
            for (int i = 0; i < floats.Length; i++)
                floats[i] = (float)BitConverter.ToDouble(bytes, i * 8);
            return floats;
        }

        private static void AddScanToRun(Scan scan, Run run)
        {
            scan.IsolationWindowLowerBoundary = scan.IsolationWindowTargetMz - scan.IsolationWindowLowerOffset;
            scan.IsolationWindowUpperBoundary = scan.IsolationWindowTargetMz + scan.IsolationWindowUpperOffset;

            if (scan.MsLevel == 1)
                run.Ms1Scans.Add(scan);
            else if (scan.MsLevel == 2)
                run.Ms2Scans.Add(scan);
            else
            {
                throw new ArgumentOutOfRangeException("scan.MsLevel", "MS Level must be 1 or 2");
            }
        }

        private static float[] FillZeroArray(float[] array)
        {
            System.Array.Resize(ref array, 5);
            return array;
        }

        private void FindMs2IsolationWindows(Run run)
        {
            run.IsolationWindows = run.Ms2Scans.Select(x => (x.IsolationWindowTargetMz - x.IsolationWindowLowerOffset, x.IsolationWindowTargetMz + x.IsolationWindowUpperOffset)).Distinct().ToList();
        }
    }
}
