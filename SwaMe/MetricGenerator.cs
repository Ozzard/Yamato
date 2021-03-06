using MzmlParser;
using System.Collections.Generic;
using System.Linq;

namespace SwaMe
{
    public class MetricGenerator
    {
        public void GenerateMetrics(Run run, int division,  string inputFilePath, bool irt, bool combine, bool lastFile, string date)
        {

            //Acquire RTDuration: last minus first
            double RTDuration = run.LastScanTime - run.StartTime;

            //Interpolate, Smooth, create chromatogram and generate chromatogram metrics
            ChromatogramMetricGenerator chromatogramMetrics = new ChromatogramMetricGenerator();
            chromatogramMetrics.GenerateChromatogram(run);

            if (irt)
                chromatogramMetrics.GenerateiRTChromatogram(run);

            //Calculating the largestswath
            double swathSizeDifference = run.Ms2Scans.Max(x => x.IsolationWindowUpperOffset - x.IsolationWindowLowerOffset);

            // This method will group the scans into swaths of the same number, return the number of swaths in a full cycle (maxswath) and call a FileMaker method to write out the metrics.
            SwathGrouper swathGrouper = new SwathGrouper { };
            SwathGrouper.SwathMetrics swathMetrics = swathGrouper.GroupBySwath(run);

            //Retrieving Density metrics
            var Density = run.Ms2Scans.OrderBy(g => g.Density).Select(g => g.Density).ToList();

            //Create IQR so you can calculate IQR:            
            RTGrouper rtGrouper = new RTGrouper { };
            RTGrouper.RTMetrics rtMetrics = rtGrouper.DivideByRT(run, division, RTDuration);
            FileMaker fileMaker = new FileMaker(division, inputFilePath, run, swathMetrics, rtMetrics, RTDuration, swathSizeDifference, run.Ms2Scans.Count(), Density.Sum(), Density.ElementAt(Density.Count() / 2), InterQuartileRangeCalculator.CalcIQR(Density), run.Ms1Scans.Count(), date);
            fileMaker.MakeUndividedMetricsFile();
            if (run.IRTPeaks != null && run.IRTPeaks.Count() > 0)
            {
                fileMaker.MakeiRTmetricsFile(run);
            }
            
            fileMaker.MakeMetricsPerRTsegmentFile(rtMetrics);
            fileMaker.MakeMetricsPerSwathFile(swathMetrics);
            fileMaker.CreateAndSaveMzqc();
            if (combine && lastFile)
            {
                fileMaker.CombineMultipleFilesIntoSingleFile(date + "_iRTMetrics_*", date + "AllIRTMetrics.tsv");
                fileMaker.CombineMultipleFilesIntoSingleFile(date + "_MetricsBySwath_*", date + "AllMetricsBySwath.tsv");
                fileMaker.CombineMultipleFilesIntoSingleFile(date + "_RTDividedMetrics_*", date + "AllRTDividedMetrics.tsv");
                fileMaker.CombineMultipleFilesIntoSingleFile(date + "_undividedMetrics_*", date + "AllUndividedMetrics.tsv");
             }
        }
    }
}
        