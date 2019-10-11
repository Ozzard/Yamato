using System;
using System.Collections.Generic;
using MzmlParser;
using NLog;
using LibraryParser;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;

namespace IRTSearcher
{
    public class IRTPeptideMatch
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static readonly Object Lock = new Object();
        const double irtTolerance = 0.5;


        private static CountdownEvent cde = new CountdownEvent(1);

        public Run ParseLibrary(Run run, string iRTpath, double massTolerance)
        {
            CheckIrtPathAccessible(iRTpath);
            run.iRTpath = iRTpath;
            run.IRTPeaks = new List<IRTPeak>();
            Library irtLibrary = new Library();
            if (run.iRTpath.EndsWith("traml", StringComparison.InvariantCultureIgnoreCase))
            {
                TraMLReader traMLReader = new TraMLReader();
                irtLibrary = traMLReader.LoadLibrary(run.iRTpath);

                run.IRTPeaks = new List<IRTPeak>();
                for (int i = 0; i < irtLibrary.PeptideList.Count; i++)
                {
                    IRTPeak peak = new IRTPeak();
                    Library.Peptide irtLibPeptide = (Library.Peptide)irtLibrary.PeptideList[i];
                    peak.ExpectedRetentionTime = irtLibPeptide.RetentionTime;
                    peak.Mz = GetTheoreticalMz(irtLibPeptide.Sequence, irtLibPeptide.ChargeState);

                    for (int transition = 0; transition < irtLibrary.TransitionList.Count; transition++)
                    {
                        if (Math.Abs(((Library.Transition)irtLibrary.TransitionList[transition]).PrecursorMz - peak.Mz) < 0.02)//chose this value as the smallest difference between two biognosis peptides is this
                        {
                            peak.AssociatedTransitions.Add((Library.Transition)irtLibrary.TransitionList[transition]);
                        }
                    }

                    run.IRTPeaks.Add(peak);
                    run.IRTPeaks = run.IRTPeaks.OrderBy(x => x.ExpectedRetentionTime).ToList();
                }

            }
            else if (run.iRTpath.EndsWith("csv", StringComparison.InvariantCultureIgnoreCase) || run.iRTpath.EndsWith("tsv", StringComparison.InvariantCultureIgnoreCase) || run.iRTpath.EndsWith("txt", StringComparison.InvariantCultureIgnoreCase))
            {
                SVReader svReader = new SVReader();
                irtLibrary = svReader.LoadLibrary(run.iRTpath);
                run.IRTPeaks = new List<IRTPeak>();
                for (int i = 0; i < irtLibrary.PeptideList.Count; i++)
                {
                    IRTPeak peak = new IRTPeak();
                    Library.Peptide irtLibPeptide = (Library.Peptide)irtLibrary.PeptideList[i];
                    peak.Mz = double.Parse(irtLibPeptide.Id.Replace(",", "."), CultureInfo.InvariantCulture);
                    for (int transition = 0; transition < irtLibrary.TransitionList.Count; transition++)
                    {
                        if (Math.Abs(((Library.Transition)irtLibrary.TransitionList[transition]).PrecursorMz - peak.Mz) < 0.02)//chose this value as the smallest difference between two biognosis peptides is this
                        {
                            peak.AssociatedTransitions.Add((Library.Transition)irtLibrary.TransitionList[transition]);
                        }
                    }
                    peak.AssociatedTransitions = irtLibPeptide.AssociatedTransitions;
                    run.IRTPeaks.Add(peak);
                }
            }
            foreach (IRTPeak peak in run.IRTPeaks.Where(x => x.PossPeaks.Count() > 0))
            {
                peak.PossPeaks = peak.PossPeaks.OrderByDescending(x => x.BasePeak.Intensity).ToList();
            }

            ReadSpectrum(run, massTolerance);
            irtSearch(run, massTolerance);
            return run;
        }

        private static double GetTheoreticalMz(string Sequence, int chargeState)
        {
            return (Sequence.Count(x => x == 'A') * 71.04 + Sequence.Count(x => x == 'H') * 137.06 + Sequence.Count(x => x == 'R') * 156.10 +
                Sequence.Count(x => x == 'K') * 128.09 + Sequence.Count(x => x == 'I') * 113.08 + Sequence.Count(x => x == 'F') * 147.07 +
                Sequence.Count(x => x == 'L') * 113.08 + Sequence.Count(x => x == 'W') * 186.08 + Sequence.Count(x => x == 'M') * 131.04 +
                Sequence.Count(x => x == 'P') * 97.05 + Sequence.Count(x => x == 'C') * 103.01 + Sequence.Count(x => x == 'N') * 114.04 +
                Sequence.Count(x => x == 'V') * 99.07 + Sequence.Count(x => x == 'G') * 57.02 + Sequence.Count(x => x == 'S') * 87.03 +
                Sequence.Count(x => x == 'Q') * 128.06 + Sequence.Count(x => x == 'Y') * 163.06 + Sequence.Count(x => x == 'D') * 115.03 +
                Sequence.Count(x => x == 'E') * 129.04 + Sequence.Count(x => x == 'T') * 101.05 + 18.02 + 2.017) / chargeState;
        }

        private static void CheckIrtPathAccessible(string iRTpath)
        {
            try
            {
                using (Stream stream = new FileStream(iRTpath, FileMode.Open))
                {
                    logger.Info("Starting the incorporation of iRT file: {0}. Please be patient.", iRTpath);
                }
            }
            catch (IOException ex)
            {
                logger.Error(ex, "The iRT file {0} was not able to be read - this can happen because it is in use by another program. Please close the application using it and try again.", iRTpath);
                throw ex;
            }
        }

        public static void ReadSpectrum(Run run, double massTolerance)
        {
            foreach (Scan scan in run.Ms1Scans.Where(x => x.Spectrum != null))
            {

                lock (Lock)//Otherwise threading adds everything to posspeaks before checking if there already is something
                {
                    foreach (IRTPeak ip in run.IRTPeaks)
                    {
                        int tempCount = scan.Spectrum.Count(x => Math.Abs(ip.Mz - x.Mz) <= massTolerance);
                        if (tempCount > 0)
                        {
                            List<SpectrumPoint> temp = scan.Spectrum.Where(x => Math.Abs(ip.Mz - x.Mz) <= massTolerance).OrderByDescending(x => x.Intensity).Take(1).ToList();
                            if (ip.PossPeaks.Count() > 1)
                            {

                                bool found = false;


                                for (int i = 0; i < ip.PossPeaks.Count() - 1; i++)
                                {

                                    if (Math.Abs(ip.PossPeaks[i].BasePeak.RetentionTime - temp[0].RetentionTime) < irtTolerance)// if there is already a possPeak that it can fit into then add
                                    {
                                        found = true;

                                        if (ip.PossPeaks[i].BasePeak.Intensity < temp[0].Intensity)
                                        {
                                            //This peak is more intense and should be the basepeak of this peak
                                            ip.PossPeaks[i].BasePeak = temp[0];
                                        }
                                        break;
                                    }
                                }
                                if (!found)
                                {
                                    PossiblePeak possPeak = new PossiblePeak();
                                    possPeak.Alltransitions = new List<List<SpectrumPoint>>();
                                    foreach (var at in ip.AssociatedTransitions)
                                    {
                                        List<SpectrumPoint> tempList = new List<SpectrumPoint>();

                                        possPeak.Alltransitions.Add(tempList);//alltransitions are used to calculate MS1 peak metrics here we create an empty vector to which we later add
                                    }
                                    possPeak.BasePeak = temp[0];
                                    ip.PossPeaks.Add(possPeak);
                                }
                            }


                            else
                            {

                                PossiblePeak possPeak = new PossiblePeak();
                                possPeak.Alltransitions = new List<List<SpectrumPoint>>();
                                foreach (var at in ip.AssociatedTransitions)
                                {
                                    List<SpectrumPoint> tempList = new List<SpectrumPoint>();
                                    possPeak.Alltransitions.Add(tempList);
                                }
                                possPeak.BasePeak = temp[0];
                                ip.PossPeaks.Add(possPeak);
                            }
                        }
                    }
                }
            }
            //lets try to find all the spectra where at least two transitions occur and add their RT's to a list.We can then later compare this list to the iRTPeak.spectrum.RT's
            lock (Lock)
            {
                foreach (IRTPeak peak in run.IRTPeaks)
                {
                    if (peak.PossPeaks.Count() > 0)
                    {
                        cde.AddCount();
                        ThreadPool.QueueUserWorkItem(state => FindMatchingTransitions(run, massTolerance, peak));
                        //FindMatchingTransitions(run, massTolerance, peak);
                    }
                    else
                    {
                        logger.Info("No matches were found for the following irtpeak:{0} ", peak.Mz);
                    }

                }
            }
            cde.Signal();
            cde.Wait();

        }

        private static void FindMatchingTransitions(Run run, double massTolerance, IRTPeak peak)
        {
            lock (Lock)
            {
                foreach (PossiblePeak pp in peak.PossPeaks.OrderByDescending(x => x.BasePeak.Intensity).ToList())
                {
                    /*List<string> stringlist = new List<string>();
                    List<Scan> matchingscans = new List<Scan>();
                    foreach (Library.Transition transition in peak.AssociatedTransitions)
                    {
                        
                        int temp = run.Ms2Scans.Count(x => x.Spectrum != null && x.IsolationWindowLowerBoundary <= (transition.ProductMz + massTolerance) && x.IsolationWindowUpperBoundary >=
                        (transition.ProductMz - massTolerance) && Math.Abs(x.ScanStartTime - pp.BasePeak.RetentionTime) < 10);
                        if (temp > 0)
                        {
                            var temp2 = run.Ms2Scans.Where(x => x.Spectrum != null && x.IsolationWindowLowerBoundary <= (transition.ProductMz + massTolerance) && x.IsolationWindowUpperBoundary >=
                              (transition.ProductMz - massTolerance) && Math.Abs(x.ScanStartTime - pp.BasePeak.RetentionTime) < irtTolerance).ToList().OrderBy(x => x.BasePeakIntensity).Last();//we cannot search through the whole run, only the ms2 scans close to that possible peak will have its fragments
                            matchingscans.Add(temp2);
                            stringlist.Add(Convert.ToString(temp2.ScanStartTime + "-" + Convert.ToString(transition.Id) + "-" + transition.ProductMz));
                
                        }
                        else if (temp == 0)
                        {
                            matchingscans.Add(null);
                        }
                    }*/
                    //Add the spectrumpoints of transitions to the transitionSpectrum of that possible peak

                    foreach (Scan scan in run.Ms2Scans)
                    {
                        if (scan != null && Math.Abs(scan.ScanStartTime - pp.BasePeak.RetentionTime) < irtTolerance)
                        {
                            for (int iterator = 0; iterator < peak.AssociatedTransitions.Count(); iterator++)
                            {
                                if (scan.Spectrum != null)
                                {
                                    int Count = scan.Spectrum.Count(i => Math.Abs(i.Mz - peak.AssociatedTransitions[iterator].ProductMz) <= massTolerance);

                                    if (Count > 0)
                                    {

                                        pp.Alltransitions[iterator].Add(scan.Spectrum.Where(i => Math.Abs(i.Mz - peak.AssociatedTransitions[iterator].ProductMz) <= massTolerance).OrderBy(x => x.Intensity).ToList().Last());

                                    }
                                }
                            }
                        }
                    }

                }
            }

            cde.Signal();

        }

        private static void irtSearch(Run run, double massTolerance)
        {
            foreach (Scan scan in run.Ms1Scans)
            {
                foreach (IRTPeak ip in run.IRTPeaks)
                {
                    if (Math.Abs(ip.RetentionTime - scan.ScanStartTime) <= massTolerance)
                    {
                        List<SpectrumPoint> temp = scan.Spectrum.Where(x => Math.Abs(ip.Mz - x.Mz) <= massTolerance).OrderByDescending(x => x.Intensity).Take(1).ToList();
                        if (temp.Count > 0) ip.Spectrum.Add(temp[0]);
                    }
                }
            }

        }
    }
}
