﻿using System;
using System.Data;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using ReportPluginFramework.Properties;
using ReportPluginFramework;
using ReportPluginFramework.ReportData;
using ReportPluginFramework.ReportData.TimeSeriesComputedStatistics;
using ReportPluginFramework.ReportData.TimeSeriesData;

using Server.Services.PublishService.ServiceModel.RequestDtos;
using Server.Services.PublishService.ServiceModel.ResponseDtos;
using Server.Services.PublishService.ServiceModel.Dtos;
using Server.Services.PublishService.ServiceModel.Dtos.FieldVisit;
using Server.Services.PublishService.ServiceModel.Dtos.FieldVisit.Enum;

using TimeSeriesPoint = ReportPluginFramework.ReportData.TimeSeriesData.TimeSeriesPoint;
using InterpolationType = ReportPluginFramework.ReportData.TimeSeriesDescription.InterpolationType;

namespace Reports
{
    public class Common
    {
        private static ServiceStack.Logging.ILog Log = ServiceStack.Logging.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private RunFileReportRequest _RunReportRequest;
        private int _WaterYearMonth = 10;
        public static string _DateFormat = "yyyy-MM-dd HH:mmzzz";

        public Dictionary<Guid, DateTimeOffsetInterval> _TimeSeriesTimeRangeIntervals = new Dictionary<Guid, DateTimeOffsetInterval>();

        public string _DllName;
        public string _DllFolder;

        public Common(RunFileReportRequest request)
        {
            _RunReportRequest = request;

            _WaterYearMonth = _RunReportRequest.ReportData.GetSystemConfiguration().WaterYearDefaultMonth;
        }

        public RunFileReportRequest RunReportRequest()
        {
            return _RunReportRequest;
        }

        public IReportData ReportData()
        {
            return _RunReportRequest.ReportData;
        }

        public IPublishGateway Publish()
        {
            return _RunReportRequest.Publish;
        }

        public int GetWaterYearMonth()
        {
            return _WaterYearMonth;
        }

        public void CheckReportDefinitionVersion(string requiredVersion)
        {
            bool containsVersionKey = _RunReportRequest.ReportDefinitionMetadata.ContainsKey("Version");

            string version = (containsVersionKey) ? _RunReportRequest.ReportDefinitionMetadata["Version"] : "1";

            if (version != requiredVersion)
            {
                string msg = string.Format(
                    "Exception due to an incompatible version of report definition: the report definition is '{0}' " +
                    "but this report requires '{1}', check the value of the " +
                    "System Configuration Tool setting ReportPluginConfig-{2}",
                    version, requiredVersion, _DllName);
                throw new Exception(msg);
            }
        }

        public TimeSpan GetReportTimeSpanOffset()
        {
            if ((_RunReportRequest.Inputs != null) && (_RunReportRequest.Inputs.TimeSeriesInputs.Count > 0))
            {
                foreach (TimeSeriesReportRequestInput timeseriesInput in _RunReportRequest.Inputs.TimeSeriesInputs)
                {
                    if (timeseriesInput.IsMaster)
                        return GetTimeSeriesOffset(timeseriesInput.UniqueId);
                }
                return GetTimeSeriesOffset(_RunReportRequest.Inputs.TimeSeriesInputs[0].UniqueId);
            }

            if ((_RunReportRequest.Inputs != null) && (_RunReportRequest.Inputs.LocationInput != null))
                return TimeSpan.FromHours(GetLocationData(_RunReportRequest.Inputs.LocationInput.Identifier).UtcOffset);

            return TimeSpan.Zero;
        }

        public DateTimeOffsetInterval GetPeriodSelectedAdjustedForReport()
        {
            DateTimeOffsetInterval interval = GetPeriodSelectedInUtcOffset(GetReportTimeSpanOffset());

            if ((_RunReportRequest.Inputs != null) &&
                (_RunReportRequest.Inputs.TimeSeriesInputs.Count == 0) &&
                (_RunReportRequest.Inputs.LocationInput != null))
            {
                interval = GetReportTimeRangeInLocationUtcOffset(_RunReportRequest.Inputs.LocationInput.Identifier);
            }

            return GroupByHandler.GetTrimmedPeriodSelected(interval);
        }

        public DataSet GetCommonDataSet(string dllName, string dllFolder)
        {
            _DllName = dllName;
            _DllFolder = dllFolder;
            Log.DebugFormat("GetCommonDataSet for dll {0} in folder {1}", dllName, dllFolder);

            AddRunReportRequestParametersFromDefinitionMetadata();

            Log.Info(ReportInputInformation());

            return GetDataTablesBuilder().GetCommonDataSet(dllName, dllFolder);
        }

        public DataTablesBuilder GetDataTablesBuilder()
        {
            return new DataTablesBuilder(_RunReportRequest, this);
        }

        public void AddRunReportRequestParametersFromDefinitionMetadata()
        {
            Dictionary<string, string> metadatas = _RunReportRequest.ReportDefinitionMetadata;

            if ((metadatas == null) || (metadatas.Count == 0))
                return;

            foreach (string key in metadatas.Keys)
            {
                Log.DebugFormat("metatdata item key = '{0}' value = '{1}'", key, metadatas[key]);
                try
                {
                    ReportJobParameter parameter = new ReportJobParameter()
                    {
                        Name = key,
                        Value = metadatas[key]
                    };
                    _RunReportRequest.Parameters.Add(parameter);
                }
                catch (Exception exp)
                {
                    Log.Error(string.Format("Error adding report parameter from metadata '{0}', value = '{1}'", key, metadatas[key]), exp);
                }
            }
        }

        public void AddRunReportRequestParametersFromSettingsFile()
        {
            string settingsFile = Path.Combine(_DllFolder, "Settings.json");
            Log.DebugFormat("AddSettings - look for settings in file = '{0}'", settingsFile);
            try
            {
                if (File.Exists(settingsFile))
                {
                    using (StreamReader reader = File.OpenText(settingsFile))
                    {
                        JObject jsonObject = (JObject)JToken.ReadFrom(new JsonTextReader(reader));
                        foreach (dynamic item in jsonObject["Parameters"])
                        {
                            Log.DebugFormat("item name = '{0}' value = '{1}'", item.Name, item.Value);
                            ReportJobParameter parameter = new ReportJobParameter()
                            {
                                Name = (string)item.Name,
                                Value = (string)item.Value
                            };
                            _RunReportRequest.Parameters.Add(parameter);
                        }
                    }
                }
            }
            catch (Exception exp)
            {
                Log.Error(string.Format("Error reading settings from '{0}'", settingsFile), exp);
            }
        }

        public int GetParameterInt(string parameterName, int defaultValue)
        {
            int retValue = defaultValue;
            try
            {
                int? parameterValue = _RunReportRequest.Parameters.GetInt(parameterName);
                if (parameterValue.HasValue) retValue = parameterValue.Value;
            }
            catch (Exception exp)
            {
                Log.Error(string.Format("Error in GetParameterInt parameterName = {0}, return defaultValue = {1}", parameterName, defaultValue), exp);
            }
            return retValue;
        }

        public double GetParameterDouble(string parameterName, double defaultValue)
        {
            double retValue = defaultValue;
            try
            {
                double? parameterValue = _RunReportRequest.Parameters.GetDouble(parameterName);
                if (parameterValue.HasValue) retValue = parameterValue.Value;
            }
            catch (Exception exp)
            {
                Log.Error(string.Format("Error in GetParameterDouble parameterName = {0}, return defaultValue = {1}", parameterName, defaultValue), exp);
            }
            return retValue;
        }

        public string GetParameterString(string parameterName, string defaultValue)
        {
            string retValue = defaultValue;
            try
            {
                string parameterValue = _RunReportRequest.Parameters.GetString(parameterName);
                if (!string.IsNullOrEmpty(parameterValue)) retValue = parameterValue;
            }
            catch (Exception exp)
            {
                Log.Error(string.Format("Error in GetParameterString parameterName = {0}, return defaultValue = {1}", parameterName, defaultValue), exp);
            }
            return retValue;
        }

        public string GetTimeSeriesIdentifierString(Guid timeseriesUniqueId, string preFix)
        {
            return preFix + ": " + GetTimeSeriesInformation(timeseriesUniqueId);
        }

        public string GetTimeSeriesInformation(Guid timeseriesUniqueId)
        {
            string retValue = GetTimeSeriesDescription(timeseriesUniqueId).Identifier;

            string locationName = GetLocationName(GetTimeSeriesDescription(timeseriesUniqueId).LocationIdentifier);

            if (!string.IsNullOrEmpty(locationName))
            {
                retValue += ", " + locationName;
            }
            return retValue;
        }

        public string GetLocationName(string locationIdentifier)
        {
            LocationDescription locationDescription = GetLocationDescriptionByIdentifier(locationIdentifier);
            return (locationDescription != null) ? locationDescription.Name : "";
        }

        public double GetTotalPointCount(Guid timeSeriesUniqueId)
        {
            bool? requiresNoCoverage = null;
            double? noCoverageAmount = null;

            List<TimeSeriesPoint> points =
                GetComputedStatisticsPoints(timeSeriesUniqueId, StatisticType.Count, StatisticPeriod.Annual, requiresNoCoverage, noCoverageAmount);

            double count = 0;
            foreach (TimeSeriesPoint point in points)
            {
                count += (point.Value.HasValue) ? point.Value.Value : 0;
            }

            return count;
        }

        public string GetTimeRangeString(Guid timeseriesUniqueId, DateTimeOffsetInterval timerange)
        {
            string dateFormat = "yyyy-MM-dd HH:mm:ss";
            string retValue = Resources.UtcOffset + ": " + GetOffsetString(GetTimeSeriesDescription(timeseriesUniqueId).UtcOffset);

            if (timerange.Start.HasValue && timerange.End.HasValue)
            {
                retValue += ", " + Resources.StartTime + ": " + timerange.Start.Value.ToString(dateFormat);
                retValue += ", " + Resources.EndTime + ": " + timerange.End.Value.ToString(dateFormat);
            }
            else
            {
                retValue += ", " + Resources.NoData;
            }
            return retValue;
        }

        public Guid? GetTimeSeriesInputByName(string timeseriesInputName)
        {
            Log.DebugFormat("GetTimeSeriesInputByName: look for input with timeseriesInputName = {0}", timeseriesInputName);
            ReportRequestInputs inputs = _RunReportRequest.Inputs;
            if (inputs == null) return null;

            foreach (TimeSeriesReportRequestInput timeseriesInput in _RunReportRequest.Inputs.TimeSeriesInputs)
                if (timeseriesInput.Name == timeseriesInputName)
                {
                    Guid uniqueId = timeseriesInput.UniqueId;
                    Log.DebugFormat("GetTimeSeriesInputByName: found input with timeseriesInputName = {0} and UniqueId = {1}", timeseriesInputName, uniqueId);
                    return uniqueId;
                }
            return null;
        }

        public TimeSeriesDescription GetTimeSeriesDescription(Guid timeseriesUniqueId)
        {
            TimeSeriesDescriptionListByUniqueIdServiceRequest tsDescRequest = new TimeSeriesDescriptionListByUniqueIdServiceRequest();
            tsDescRequest.TimeSeriesUniqueIds = new List<Guid>() { timeseriesUniqueId };
            TimeSeriesDescriptionListByUniqueIdServiceResponse tsDescResponse = Publish().Get(tsDescRequest);
            if (tsDescResponse.TimeSeriesDescriptions.Count > 0)
                return tsDescResponse.TimeSeriesDescriptions[0];

            Log.InfoFormat("GetTimeSeriesDescription for guid = {0} not found, returning null", timeseriesUniqueId);
            return null;
        }

        public TimeSeriesDescription GetTimeSeriesDescription(string locIdentifier, string parameter, string label)
        {
            try
            {
                TimeSeriesDescriptionServiceRequest request = new TimeSeriesDescriptionServiceRequest();

                request.LocationIdentifier = locIdentifier;
                request.Parameter = parameter;

                TimeSeriesDescriptionListServiceResponse response = Publish().Get(request);

                foreach (TimeSeriesDescription tsDesc in response.TimeSeriesDescriptions)
                    if (tsDesc.Label == label) return tsDesc;
            }
            catch { }

            return null;
        }

        public LocationDescription GetLocationDescriptionByIdentifier(string locationIdentifier)
        {
            var locationDescriptionListRequest = new LocationDescriptionListServiceRequest();
            locationDescriptionListRequest.LocationIdentifier = locationIdentifier;
            var locationDescriptions = Publish().Get(locationDescriptionListRequest).LocationDescriptions;
            return (locationDescriptions.Count > 0) ? locationDescriptions[0] : null;
        }

        public LocationDataServiceResponse GetLocationData(string locationIdentifier)
        {
            var locationDataRequest = new LocationDataServiceRequest();
            locationDataRequest.LocationIdentifier = locationIdentifier;
            return Publish().Get(locationDataRequest);
        }

        public ParameterMetadata GetParameterMetadata(string parameterName)
        {
            var request = new ParameterListServiceRequest();
            var parameters = Publish().Get(request).Parameters;
            foreach (ParameterMetadata parameterMetaData in parameters)
                if (parameterMetaData.Identifier == parameterName)
                    return parameterMetaData;

            Log.InfoFormat("GetParameterMetadata for parameterName = {0} not found, returning null", parameterName);
            return null;
        }

        public string GetPeriodSelectedInformation(DateTimeOffsetInterval interval)
        {
            return Resources.PeriodSelected + ": " + PeriodSelectedString(interval);
        }

        public string GetTimeSeriesUnitInformation(Guid timeseriesUniqueId)
        {
            return Resources.Units + ": " + GetTimeSeriesUnitSymbol(timeseriesUniqueId);
        }

        public string GetTimeSeriesUnitSymbol(Guid timeseriesUniqueId)
        {
            return GetUnitSymbol(GetTimeSeriesDescription(timeseriesUniqueId).Unit);
        }

        public string GetUnitSymbol(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return "";

            var units = ReportData().GetUnits();
            foreach (Unit u in units) if (unitId == u.Identifier) return u.Symbol;
            return unitId;
        }

        public string GetGradeDisplayName(int? gradeCode)
        {
            GradeMetadata gradeMetadata = GetGradeMetadata(gradeCode);
            if (gradeMetadata != null) return gradeMetadata.DisplayName;

            return "";
        }

        public string GetGradeDisplayName(string gradeCode)
        {
            GradeMetadata gradeMetadata = GetGradeMetadata(gradeCode);
            if (gradeMetadata != null) return gradeMetadata.DisplayName;

            return "";
        }

        public GradeMetadata GetGradeMetadata(int? gradeCode)
        {
            if (!gradeCode.HasValue) return null;

            return GetGradeMetadata(gradeCode.Value.ToString());
        }

        public GradeMetadata GetGradeMetadata(string gradeCode)
        {
            if (string.IsNullOrEmpty(gradeCode)) return null;

            var request = new GradeListServiceRequest();
            var grades = Publish().Get(request).Grades;
            foreach (GradeMetadata gradeMetaData in grades)
                if (gradeMetaData.Identifier == gradeCode)
                    return gradeMetaData;

            Log.InfoFormat("GetGradeMetadata for gradeCode = {0} not found, returning null", gradeCode);
            return null;
        }

        public double GetPercentUncertainty(DischargeUncertainty dischargeUncertainty)
        {
            if (dischargeUncertainty.ActiveUncertaintyType == UncertaintyType.None)
                return double.NaN;
            if (dischargeUncertainty.ActiveUncertaintyType == UncertaintyType.Quantitative)
                return (dischargeUncertainty.QuantitativeUncertainty.Numeric.HasValue) ?
                    dischargeUncertainty.QuantitativeUncertainty.Numeric.Value : double.NaN;
            if (dischargeUncertainty.ActiveUncertaintyType == UncertaintyType.Qualitative)
                return GetPercentUncertaintyForQualitativeGrade(dischargeUncertainty.QualitativeUncertainty);
            return double.NaN;
        }

        public double GetPercentUncertaintyForQualitativeGrade(QualitativeUncertaintyType qualitativeUncertainty)
        {
            switch (qualitativeUncertainty)
            {
                case QualitativeUncertaintyType.Excellent:
                    return 2;
                case QualitativeUncertaintyType.Good:
                    return 5;
                case QualitativeUncertaintyType.Fair:
                    return 8;
                case QualitativeUncertaintyType.Poor:
                    return 10;
            }
            return double.NaN;
        }

        public string PeriodSelectedString(DateTimeOffsetInterval interval)
        {
            string timeRange;
            if (!interval.Start.HasValue && !interval.End.HasValue)
            {
                timeRange = Resources.EntireRecord;
            }
            else if (!interval.Start.HasValue)
            {
                timeRange = string.Format(Resources.FromBeginningOfRecordToX, FormatDateTimeOffset(interval.End.Value));
            }
            else if (!interval.End.HasValue)
            {
                timeRange = string.Format(Resources.XToEndOfRecord, FormatDateTimeOffset(interval.Start.Value));
            }
            else
            {
                var localStartTime = interval.Start.Value;
                var localEndTime = localStartTime == interval.End.Value ? interval.End.Value : interval.End.Value.AddTicks(-1);
                timeRange = FormattableString.Invariant($"{FormatDateTimeOffset(localStartTime)} - {FormatDateTimeOffset(localEndTime)}");
            }
            return timeRange;
        }

        public string FormatDateTimeOffset(DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToString("yyyy-MM-dd HH:mm");
        }

        public TimeSpan GetTimeSeriesOffset(Guid timeseriesUniqueId)
        {
            TimeSeriesDescription tsd = GetTimeSeriesDescription(timeseriesUniqueId);
            return TimeSpan.FromHours(tsd.UtcOffset);
        }

        public string GetOffsetString(double offset)
        {
            TimeSpan tsSpan = TimeSpan.FromHours(offset);
            return GetOffsetString(tsSpan);
        }

        public string GetOffsetString(TimeSpan offset)
        {
            return ((offset < TimeSpan.Zero) ? "-" : "+") + offset.ToString(@"hh\:mm");
        }

        public DateTimeOffsetInterval GetReportTimeRangeInTimeSeriesUtcOffset(Guid timeseriesUniqueId)
        {
            TimeSpan tsOffset = GetTimeSeriesOffset(timeseriesUniqueId);

            return GetPeriodSelectedInUtcOffset(tsOffset);
        }

        public DateTimeOffsetInterval GetReportTimeRangeInLocationUtcOffset(string locationIdentifier)
        {
            TimeSpan tsOffset = TimeSpan.FromHours(GetLocationData(locationIdentifier).UtcOffset);

            DateTimeOffsetInterval interval = GetPeriodSelectedInUtcOffset(tsOffset);

            DateTimeOffset? startTime = null;
            DateTimeOffset? endTime = null;

            if (interval.Start.HasValue) startTime = interval.Start.Value.Subtract(tsOffset);
            if (interval.End.HasValue) endTime = interval.End.Value.Subtract(tsOffset);

            return new DateTimeOffsetInterval(startTime, endTime);
        }

        public DateTimeOffsetInterval GetPeriodSelectedInUtcOffset(TimeSpan utcOffset)
        {
            return GetIntervalInUtcOffset(_RunReportRequest.Interval, utcOffset);
        }

        public bool PeriodSelectedIsWaterYear()
        {
            return PeriodSelectedIsWaterYear(GetReportTimeSpanOffset());
        }
        public bool PeriodSelectedIsWaterYear(TimeSpan utcOffset)
        {
            DateTimeOffset? intervalStartTime = GetPeriodSelectedInUtcOffset(utcOffset).Start;
            if (intervalStartTime.HasValue)
            {
                Log.DebugFormat("PeriodSelectedIsWaterYear waterYearMonth = {0}, periodSelected = {1}, Period selected in offset = {2} is {3}",
                  GetWaterYearMonth(), TimeRangeString(_RunReportRequest.Interval), utcOffset, TimeRangeString(GetPeriodSelectedInUtcOffset(utcOffset)));
            }
            return (intervalStartTime.HasValue) && (intervalStartTime.Value.Month == GetWaterYearMonth());
        }

        public DateTimeOffsetInterval GetIntervalInUtcOffset(DateTimeOffsetInterval interval, TimeSpan utcOffset)
        {
            DateTimeOffset? startTime = interval.Start;
            DateTimeOffset? endTime = interval.End;

            if (startTime.HasValue) startTime = startTime.Value.ToOffset(utcOffset);
            if (endTime.HasValue) endTime = endTime.Value.ToOffset(utcOffset);

            DateTimeOffsetInterval newInterval = new DateTimeOffsetInterval(startTime, endTime);

            Log.DebugFormat("GetIntervalInUtcOffset request interval = {0}, utcOffset = {1}, returns interval = {2}",
                TimeRangeString(interval), utcOffset, TimeRangeString(newInterval));

            return newInterval;
        }

        public string TimeSeriesRangeString(DateTimeOffsetInterval interval)
        {
            if (!interval.Start.HasValue && !interval.End.HasValue)
                return Resources.NoPoints;
            return TimeIntervalAsString(interval, _DateFormat);
        }

        public string TimeRangeString(DateTimeOffsetInterval interval)
        {
            return TimeIntervalAsString(interval, _DateFormat);
        }

        public string TimeIntervalAsString(DateTimeOffsetInterval interval, string dateFormat)
        {
            DateTimeOffset? startTime = interval.Start;
            DateTimeOffset? endTime = interval.End;

            string startString = (startTime.HasValue) ? startTime.Value.ToString(dateFormat) : "Start of Record";
            string endString = (endTime.HasValue) ? endTime.Value.ToString(dateFormat) : "End of Record";
            Log.DebugFormat("TimeRangeString: interval time range is {0} to {1}", startString, endString);

            return startString + " - " + endString;
        }

        public CoverageOptions GetCoverageOptions()
        {
            double dataCoverage = GetParameterDouble("DataCoverageThresholdPercent", -1.0);

            bool useCoverage = (dataCoverage < 0) ? false : true;
            double coverageAmount = dataCoverage / 100.0;
            coverageAmount = (coverageAmount < 0.0) ? 0.0 : ((coverageAmount > 1.0) ? 1.0 : coverageAmount);

            return new CoverageOptions
            {
                CoverageThreshold = coverageAmount,
                RequiresMinimumCoverage = useCoverage
            };
        }

        public string GetCoverageString(CoverageOptions coverageOptions)
        {
            bool useCoverage = (coverageOptions.RequiresMinimumCoverage.HasValue) ? coverageOptions.RequiresMinimumCoverage.Value : false;
            double coverageAmount = (coverageOptions.CoverageThreshold.HasValue) ? coverageOptions.CoverageThreshold.Value : 0.0;
            return string.Format(Resources.DataCoverageThreshold + ": {0}", (useCoverage) ?
                (coverageAmount * 100.0).ToString() + "%" : Resources.NotApplicableAbbreviated);
        }

        public StatisticType GetStatistic(string statistic, StatisticType defaultStatisticType)
        {
            try
            {
                return (StatisticType)Enum.Parse(typeof(StatisticType), statistic);
            }
            catch { }

            return defaultStatisticType;
        }

        public List<TimeSeriesPoint> GetComputedStatisticsPoints(Guid timeseriesUniqueId,
            StatisticType statType, StatisticPeriod period, bool? requiresCoverage, double? coverageAmount)
        {
            return GetComputedStatisticsPoints(timeseriesUniqueId, null, null, statType, period, requiresCoverage, coverageAmount);
        }

        public List<TimeSeriesPoint> GetComputedStatisticsPoints(Guid timeseriesUniqueId, DateTimeOffset? startTime, DateTimeOffset? endTime,
            StatisticType statType, StatisticPeriod period, bool? requiresCoverage, double? coverageAmount)
        {
            return GetComputedStatisticsPoints(timeseriesUniqueId, startTime, endTime, statType, period, null, requiresCoverage, coverageAmount);
        }

        public List<TimeSeriesPoint> GetComputedStatisticsPoints(Guid timeseriesUniqueId, DateTimeOffset? startTime, DateTimeOffset? endTime,
            StatisticType statType, StatisticPeriod period, int? periodCount, bool? requiresCoverage, double? coverageAmount)
        {
            return GetComputedStatisticsResponse(timeseriesUniqueId, startTime, endTime, statType, period, periodCount, requiresCoverage, coverageAmount).Points;
        }

        public TimeSeriesComputedStatisticsResponse GetComputedStatisticsResponse(Guid timeseriesUniqueId, DateTimeOffset? startTime, DateTimeOffset? endTime,
    StatisticType statType, StatisticPeriod period, int? periodCount, bool? requiresCoverage, double? coverageAmount)
        {
            Log.DebugFormat("GetComputedStatisticsResponse stat = {0}, period = {1}, periodCount = {2} for TimeRange = '{3}' - '{4}'", statType, period, periodCount, startTime, endTime);

            CoverageOptions co = new CoverageOptions();
            co.RequiresMinimumCoverage = requiresCoverage;
            co.CoverageThreshold = coverageAmount;
            co.PartialCoverageGradeCode = -1;

            TimeSeriesComputedStatisticsRequest compRequest = new TimeSeriesComputedStatisticsRequest();
            compRequest.TimeSeriesUniqueId = timeseriesUniqueId;
            compRequest.StatisticType = statType;
            compRequest.StatisticPeriod = period;
            compRequest.StatisticPeriodCount = periodCount;
            compRequest.CoverageOptions = co;
            compRequest.QueryFromTime = startTime;
            compRequest.QueryToTime = (endTime.HasValue) ? endTime.Value.AddMilliseconds(-1) : endTime;

            return ReportData().GetTimeSeriesComputedStatistics(compRequest);
        }

        public List<int> GetComputedStatisticsGrades(Guid timeseriesUniqueId, DateTimeOffset? startTime, DateTimeOffset? endTime,
            StatisticType statType, StatisticPeriod period, int? periodCount, bool? requiresCoverage, double? coverageAmount)
        {
            TimeSeriesComputedStatisticsResponse response = GetComputedStatisticsResponse(timeseriesUniqueId,
                startTime, endTime, statType, period, periodCount, requiresCoverage, coverageAmount);

            List<TimeSeriesPoint> points = response.Points;
            List<GradeTimeRange> gradeRanges = response.GradeRanges;

            return GetTimeSeriesPointsGrades(points, gradeRanges);
        }

        public List<int> GetTimeSeriesPointsGrades(List<TimeSeriesPoint> points, List<GradeTimeRange> gradeRanges)
        {
            var pointwiseGrades = new List<int>();
            var gradeIndex = 0;

            foreach (var point in points)
            {
                int grade = -1;
                while (gradeIndex < gradeRanges.Count && gradeRanges[gradeIndex].EndTime <= point.Timestamp) ++gradeIndex;
                if ((gradeIndex < gradeRanges.Count) && (gradeRanges[gradeIndex].StartTime <= point.Timestamp)) grade = gradeRanges[gradeIndex].GradeCode;
                pointwiseGrades.Add(grade);
            }
            return pointwiseGrades;
        }

        public string GetGradeMarker(int code)
        {
            foreach (ReportJobParameter parm in _RunReportRequest.Parameters)
            {
                if (parm.Name.StartsWith("GradeMarker"))
                {
                    string gradeCode = parm.Name.Replace("GradeMarker", "").Trim();

                    if (gradeCode == code.ToString())
                    {
                        return parm.Value;
                    }
                }
            }
            return "";
        }

        public string GetTimeSeriesInterpolationTypeString(Guid timeseriesUniqueId)
        {
            InterpolationType interpolationType = GetTimeSeriesInterpolationType(timeseriesUniqueId);
            string interpolationTypeString = GetLocalizedEnumValue(interpolationType.GetType().Name, interpolationType.ToString());
            return string.Format("{0} - {1}", (int)interpolationType, interpolationTypeString);
        }

        public int GetBinAdjustment(Guid timeseriesUniqueId)
        {
            return (HasEndBinInterpolationType(timeseriesUniqueId)) ? -1 : 0;
        }

        public bool HasEndBinInterpolationType(Guid timeseriesUniqueId)
        {
            return IsEndBinInterpolationType(GetTimeSeriesInterpolationType(timeseriesUniqueId));
        }

        public InterpolationType GetTimeSeriesInterpolationType(Guid timeseriesUniqueId)
        {
            return ReportData().GetTimeSeriesDescription(timeseriesUniqueId).InterpolationType;
        }

        public bool IsEndBinInterpolationType(InterpolationType interpolationType)
        {
            return (interpolationType == InterpolationType.InstantaneousTotals ||
                   interpolationType == InterpolationType.PrecedingTotals) ? true : false;
        }

        public DateTimeOffsetInterval GetTimeSeriesTimeRange(Guid timeseriesUniqueId)
        {
            Log.DebugFormat("Get the time-range for uniqueId = {0}", timeseriesUniqueId);

            if (!_TimeSeriesTimeRangeIntervals.ContainsKey(timeseriesUniqueId))
            {
                Log.DebugFormat("Find the time-range for uniqueId = {0}", timeseriesUniqueId);
                DateTimeOffsetInterval timeseriesInterval = (new TimeSeriesTimeRangeFinder(this)).FindTimeSeriesTimeRange(timeseriesUniqueId);
                _TimeSeriesTimeRangeIntervals.Add(timeseriesUniqueId, timeseriesInterval);
            }

            DateTimeOffsetInterval interval = _TimeSeriesTimeRangeIntervals[timeseriesUniqueId];

            Log.DebugFormat("Time-range for uniqueId = {0} is '{1}'", timeseriesUniqueId, TimeSeriesRangeString(interval));

            return interval;
        }

        public string GetTimeSeriesTimeRangeString(Guid timeseriesUniqueId)
        {
            DateTimeOffsetInterval interval = GetTimeSeriesTimeRange(timeseriesUniqueId);
            return TimeSeriesRangeString(interval);
        }

        public string GetTimeSeriesTimeRangeInformation(Guid timeseriesUniqueId)
        {
            DateTimeOffsetInterval interval = GetTimeSeriesTimeRange(timeseriesUniqueId);
            return GetTimeRangeString(timeseriesUniqueId, interval);
        }

        public List<TimeSeriesPoint> GetTimeSeriesPoints(Guid timeseriesUniqueId, DateTimeOffset? StartTime, DateTimeOffset? EndTime)
        {
            return GetTimeSeriesDataResponse(timeseriesUniqueId, StartTime, EndTime).Points;
        }

        public TimeSeriesDataResponse GetTimeSeriesDataResponse(Guid timeseriesUniqueId, DateTimeOffset? StartTime, DateTimeOffset? EndTime)
        {
            Log.DebugFormat("GetTimeSeriesDataResponse uniqueId = {0} from {1} to {2}", timeseriesUniqueId,
                (StartTime.HasValue) ? StartTime.Value.ToString(_DateFormat) : "start of record",
                (EndTime.HasValue) ? EndTime.Value.ToString(_DateFormat) : "end of record");

            TimeSeriesDataRequest request = new TimeSeriesDataRequest();
            request.TimeSeriesUniqueId = timeseriesUniqueId;
            request.QueryFromTime = StartTime;
            request.QueryToTime = EndTime;
            request.TimeSeriesDataType = TimeSeriesDataType.Corrected;
            request.IncludeGapMarkers = true;

            return ReportData().GetTimeSeriesData(request);
        }

        public TimeSeriesDataServiceResponse GetTimeSeriesCorrectedDataReponse(Guid uniqueId, string getParts, 
            DateTimeOffset? StartTime, DateTimeOffset? EndTime)
        {
            TimeSeriesDataCorrectedServiceRequest request = new TimeSeriesDataCorrectedServiceRequest();
            request.ApplyRounding = true;
            request.GetParts = getParts;
            request.IncludeGapMarkers = true;
            request.TimeSeriesUniqueId = uniqueId;
            request.QueryFrom = StartTime;
            request.QueryTo = EndTime;

            return Publish().Get(request);
        }

        public TimeSeriesDataServiceResponse GetTimeSeriesRawDataReponse(Guid uniqueId, string getParts,
            DateTimeOffset? StartTime, DateTimeOffset? EndTime)
        {
            TimeSeriesDataRawServiceRequest request = new TimeSeriesDataRawServiceRequest();
            request.ApplyRounding = true;
            request.GetParts = getParts;
            request.TimeSeriesUniqueId = uniqueId;
            request.QueryFrom = StartTime;
            request.QueryTo = EndTime;

            return Publish().Get(request);
        }

        public List<TimeAlignedPoint> GetTimeAlignedPoints(List<Guid> inputGuids, DateTimeOffset? StartTime, DateTimeOffset? EndTime)
        {
            Log.DebugFormat("GetTimeAlignedPoints number of guids='{0}', from start = {1} to end = {2}", inputGuids.Count,
                (StartTime.HasValue) ? StartTime.Value.ToString(_DateFormat) : "start of record",
                (EndTime.HasValue) ? EndTime.Value.ToString(_DateFormat) : "end of record");

            TimeAlignedDataServiceRequest request = new TimeAlignedDataServiceRequest();
            request.TimeSeriesUniqueIds = inputGuids;
            request.QueryFrom = StartTime;
            request.QueryTo = EndTime;
            request.IncludeGapMarkers = true;

            List<TimeAlignedPoint> points = Publish().Get(request).Points;

            if (points.Count == 0)
                Log.Debug("GetTimeAlignedPoints returns zero points");
            else
                Log.DebugFormat("GetTimeAlignedPoints returns = {0} points, from first timestamp = {1} to last timestamp = {2}",
                  points.Count, points[0].Timestamp, points[points.Count - 1].Timestamp);

            return points;
        }

        public TimeAlignedPoint[] GetInstMinMaxPoints(Guid inputGuid, string interval, bool extrema, DateTimeOffset StartTime, DateTimeOffset EndTime)
        {
            TimeSeriesInstMinMaxFinder instMinMax = new TimeSeriesInstMinMaxFinder(this);
            return instMinMax.GetInstMinMaxPoints(inputGuid, interval, extrema, StartTime, EndTime);
        }

        public string RoundDouble(double value, bool fix, int places, int? limitPlaces, string missingStr)
        {
            return FormatDoubleValue(value, fix, places, missingStr);
        }

        public static string FormatDoubleValue(double value, bool fix, int places, string missingStr)
        {
            return DoubleValueFormatter.FormatDoubleValue(value, fix, places, missingStr);
        }

        public static string FormatSigFigsNumber(double value, int sigfigs)
        {
            return DoubleValueFormatter.FormatSigFigsNumber(value, sigfigs);
        }

        public RatingModelDescription GetRatingModelDescription(string ratingModelIdentifier, string locationIdentifier)
        {
            RatingModelDescriptionListServiceRequest ratingModelDescriptionListRequest = new RatingModelDescriptionListServiceRequest();
            ratingModelDescriptionListRequest.LocationIdentifier = locationIdentifier;

            RatingModelDescriptionListServiceResponse ratingModelDescriptionListResponse = Publish().Get(ratingModelDescriptionListRequest);

            RatingModelDescription ratingModelDescription = null;
            foreach (RatingModelDescription rmDesc in ratingModelDescriptionListResponse.RatingModelDescriptions)
            {
                if (rmDesc.Identifier == ratingModelIdentifier)
                {
                    ratingModelDescription = rmDesc;
                    break;
                }
            }

            return ratingModelDescription;
        }

        public RatingCurveListServiceResponse GetRatingCurveList(string ratingModelIdentifier)
        {
            RatingCurveListServiceResponse ratingCurveResponse = null;

            RatingCurveListServiceRequest ratingCurveListRequest = new RatingCurveListServiceRequest();
            ratingCurveListRequest.RatingModelIdentifier = ratingModelIdentifier;

            try
            {
                ratingCurveResponse = Publish().Get(ratingCurveListRequest);
            }
            catch { }

            return ratingCurveResponse;
        }

        public string GetRatingCurveId(string ratingModelIdentifier, DateTimeOffset time)
        {
            string ratingCurveId = "";
            try
            {
                RatingCurveListServiceRequest curveRequest = new RatingCurveListServiceRequest();
                curveRequest.RatingModelIdentifier = ratingModelIdentifier;
                curveRequest.QueryFrom = time;
                curveRequest.QueryTo = time;

                RatingCurveListServiceResponse curveResponse = Publish().Get(curveRequest);
                if (curveResponse.RatingCurves.Count > 0)
                    ratingCurveId = curveResponse.RatingCurves[0].Id;
            }
            catch { }

            return ratingCurveId;
        }

        public string ReportInputInformation()
        {
            string newLine = Environment.NewLine;
            string dateFormat = "yyyy-MM-dd HH:mm:ss.ffffffzzz";
            RunFileReportRequest runReportRequest = _RunReportRequest;

            string outputFormatMessage = string.Format("{0}OutputFormat: {1}", System.Environment.NewLine, System.Environment.NewLine);
            outputFormatMessage += string.Format("OutputFormat = '{0}'{1}", _RunReportRequest.OutputFormat, System.Environment.NewLine);
            string localeMessage = string.Format("{0}Locale: {1}", System.Environment.NewLine, System.Environment.NewLine);
            localeMessage += string.Format("Locale = '{0}'{1}", _RunReportRequest.Locale, System.Environment.NewLine);

            string message = string.Format("{0}Report: {1}", newLine, _DllName);
            message += string.Format("{0}RunReportRequest Interval: {1}", newLine, TimeIntervalAsString(_RunReportRequest.Interval, dateFormat));
            message += string.Format("{0}Period Selected Adjusted for Report: {1}", newLine, TimeIntervalAsString(GetPeriodSelectedAdjustedForReport(), dateFormat));
            message += string.Format("{0}Formatted Period Selected: ", newLine);
            message += string.Format("{0}{1}Report offset: {2}",
                PeriodSelectedString(GetPeriodSelectedAdjustedForReport()), newLine, GetOffsetString(GetReportTimeSpanOffset()));
            message += string.Format("{0}TimeSeriesInputs: {1}", newLine, newLine);
            if ((runReportRequest.Inputs != null) && (runReportRequest.Inputs.TimeSeriesInputs != null))
                foreach (TimeSeriesReportRequestInput timeseries in runReportRequest.Inputs.TimeSeriesInputs)
                    message += string.Format("Name = '{0}', UniqueId = '{1}', IsMaster= '{2}', Identifier= '{3}', utcOffset= {4}{5}",
                        timeseries.Name, timeseries.UniqueId, timeseries.IsMaster,
                        GetTimeSeriesDescription(timeseries.UniqueId).Identifier,
                        GetOffsetString(GetTimeSeriesDescription(timeseries.UniqueId).UtcOffset), newLine);

            message += string.Format("{0}LocationInput: {1}", newLine, newLine);
            if ((runReportRequest.Inputs != null) && (runReportRequest.Inputs.LocationInput != null))
                message += string.Format("Name = '{0}', Identifier = '{1}', utcOffset = {2}{3}",
                    runReportRequest.Inputs.LocationInput.Name, runReportRequest.Inputs.LocationInput.Identifier,
                    GetOffsetString(GetLocationData(runReportRequest.Inputs.LocationInput.Identifier).UtcOffset), newLine);

            message += outputFormatMessage;
            message += localeMessage;

            message += string.Format("{0}Report Settings: {1}", newLine, newLine);
            if (runReportRequest.Parameters != null)
                foreach (ReportJobParameter parameter in runReportRequest.Parameters)
                    message += string.Format("parameter: '{0}' = '{1}'{2}", parameter.Name, parameter.Value, newLine);

            message += string.Format("{0}Report Definition Metadata: {1}", newLine, newLine);
            Dictionary<string, string> metadatas = runReportRequest.ReportDefinitionMetadata;
            if ((metadatas != null) && (metadatas.Count > 0))
            {
                foreach (string key in metadatas.Keys)
                    message += string.Format("metadata: '{0}' = '{1}'{2}", key, metadatas[key], newLine);
            }

            return message;
        }

        public static string GetLocalizedEnumValue(string enumType, string enumValue)
        {
            try
            {
                string localizedEnum = Resources.ResourceManager.GetString(enumType + enumValue);
                if (string.IsNullOrEmpty(localizedEnum)) localizedEnum = Resources.ResourceManager.GetString(enumValue);
                if (string.IsNullOrEmpty(localizedEnum)) localizedEnum = enumValue;

                return localizedEnum;
            }
            catch
            {
                Log.DebugFormat("GetLocalizedEnumValue catch exception: Enum type = {0}, Enum value = {1}, returning Enum value = {1}", enumType, enumValue);
                return enumValue;
            }
        }
        public static string GetLocalizedDisplayString(string stringToLocalize)
        {
            try
            {
                string localized = Resources.ResourceManager.GetString(stringToLocalize);
                if (!string.IsNullOrEmpty(localized)) return localized;

                localized = Resources.ResourceManager.GetString(stringToLocalize.Replace(" ", string.Empty));
                if (!string.IsNullOrEmpty(localized)) return localized;
            }
            catch { }
            return stringToLocalize;
        }

        public static string GetLocalizedTimeSeriesTypeName(string timeSeriesType)
        {
            return GetLocalizedEnumValue("TimeSeriesType", timeSeriesType.Replace("Processor", ""));
        }

        public static string GetLocalizedRatingCurveTypeName(RatingCurveType ratingCurveType)
        {
            return GetLocalizedEnumValue("", ratingCurveType.ToString());
        }

        public static string GetLocalizedStatisticName(StatisticType statistic)
        {
            return GetLocalizedEnumValue("", statistic.ToString());
        }

        public static bool MatchPartialNameFilter(string filter, string stringValue)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            if (string.IsNullOrEmpty(stringValue)) stringValue = "";

            string matchThisString = "^" + filter.Trim().Replace("*", ".*") + "$";
            Regex regex = new Regex(matchThisString, RegexOptions.IgnoreCase);
            Match regMatch = regex.Match(stringValue);

            return regMatch.Success;
        }


        public string GetParameterRoundingSpec(string parameterName)
        {
            ParameterListServiceRequest request = new ParameterListServiceRequest();
            List<ParameterMetadata> parameterList = Publish().Get(request).Parameters;
            foreach (ParameterMetadata parameter in parameterList)
            {
                if (parameter.Identifier == parameterName)
                    return parameter.RoundingSpec;
            }
            Log.DebugFormat("GetParameterRoundingSpec did not find parameter = '{0}'", parameterName);
            return "";
        }

        public string GetFormattedDouble(double value, string roundingSpec, string missingStr = null)
        {
            if (string.IsNullOrEmpty(roundingSpec)) roundingSpec = "DEC(3)";

            var request = new RoundServiceSpecRequest
            {
                Data = new List<double> { value },
                RoundingSpec = roundingSpec,
                ValueForNaN = missingStr
            };

            return Publish().Put(request).Data[0];
        }

        public string GetFormattedDouble(double value, string parameterDisplayId, string unitId, string missingStr)
        {
            var request = new RoundServiceRequest
            {
                Data = new List<double> { value },
                ParameterDisplayId = parameterDisplayId,
                UnitId = unitId,
                ValueForNaN = missingStr
            };

            try
            {
                return Publish().Put(request).Data[0];
            }
            catch
            {
                return GetFormattedDouble(value, "", missingStr);
            }
        }

        public List<string> GetFormattedDoubles(List<double> values, string roundingSpec, string missingStr = null)
        {
            if (string.IsNullOrEmpty(roundingSpec)) roundingSpec = "DEC(3)";

            var request = new RoundServiceSpecRequest
            {
                Data = values,
                RoundingSpec = roundingSpec,
                ValueForNaN = missingStr
            };

            return Publish().Put(request).Data;
        }

        public List<string> GetFormattedDoubles(List<double> values, string parameterDisplayId, string unitId, string missingStr)
        {
            if (values.Count == 0) return new List<string>();

            var request = new RoundServiceRequest
            {
                Data = values,
                ParameterDisplayId = parameterDisplayId,
                UnitId = unitId,
                ValueForNaN = missingStr
            };

            try
            {
                return Publish().Put(request).Data;
            }
            catch (Exception exp)
            {
                string defaultRoundingSpec = "DEC(3)";

                Log.Error(string.Format(
                    "Error rounding doubles with parameter = '{0}' and unitId = '{1}', retry with default specification {2}",
                    parameterDisplayId, unitId, defaultRoundingSpec), exp);

                return GetFormattedDoubles(values, defaultRoundingSpec, missingStr);
            }
        }

        public List<string> GetComputedStatisticsFormatted(List<TimeSeriesPoint> points, string parameterDisplayId, string unitId, string missingStr, StatisticType statisticType)
        {
            List<double> statisticPoints = new List<double>();
            foreach (TimeSeriesPoint point in points)
                statisticPoints.Add((point.Value.HasValue) ? point.Value.Value : double.NaN);

            if (statisticType == StatisticType.Count)
                return GetFormattedDoubles(statisticPoints, "DEC(0)", missingStr);

            return GetFormattedDoubles(statisticPoints, parameterDisplayId, unitId, missingStr);
        }

        public double GetDefinedShift(string ratingModelIdentifier, double stage, DateTimeOffset measurementTime)
        {
            double definedShift = double.NaN;
            try
            {
                RatingModelEffectiveShiftsByStageValuesServiceRequest request = new RatingModelEffectiveShiftsByStageValuesServiceRequest();
                request.RatingModelIdentifier = ratingModelIdentifier;
                request.MeasurementTime = measurementTime;
                request.StageValues = new List<double> { stage };

                RatingModelEffectiveShiftsByStageValuesServiceResponse response = Publish().Get(request);

                if (response.EffectiveShiftValues.Count > 0 && response.EffectiveShiftValues[0].HasValue)
                    definedShift = response.EffectiveShiftValues[0].Value;

            }
            catch (Exception exp)
            {
                Log.Error(string.Format("Exception calculating defined shift from '{0}', '{1}', '{2}': ",
                  ratingModelIdentifier, stage, measurementTime), exp);
            }
            return definedShift;
        }

        public double GetCalculatedStage(string ratingModelIdentifier, double discharge, DateTimeOffset measurementTime)
        {
            double calculatedStage = double.NaN;
            try
            {
                RatingModelInputValuesServiceRequest request = new RatingModelInputValuesServiceRequest();
                request.RatingModelIdentifier = ratingModelIdentifier;
                request.OutputValues = new List<double> { discharge };
                request.EffectiveTime = measurementTime;
                RatingModelInputValuesServiceResponse response = Publish().Get(request);
                List<double?> inputValues = response.InputValues;
                if ((inputValues.Count > 0) && (inputValues[0] != null))
                    calculatedStage = (double)inputValues[0];

            }
            catch (Exception exp)
            {
                Log.Error(string.Format("Exception calculating stage from '{0}', '{1}', '{2}': ",
                  ratingModelIdentifier, discharge, measurementTime), exp);
            }
            return calculatedStage;
        }

        public double GetCalculatedDischarge(string ratingModelIdentifier, double stage, DateTimeOffset measurementTime, bool applyShifts)
        {
            double predictedDischarge = double.NaN;
            try
            {
                RatingModelOutputValuesServiceRequest request = new RatingModelOutputValuesServiceRequest();
                request.RatingModelIdentifier = ratingModelIdentifier;
                request.InputValues = new List<double> { stage };
                request.ApplyShifts = applyShifts;
                request.EffectiveTime = measurementTime;
                RatingModelOutputValuesServiceResponse response = Publish().Get(request);
                List<double?> outputValues = response.OutputValues;
                if ((outputValues.Count > 0) && (outputValues[0] != null))
                    predictedDischarge = (double)outputValues[0];
            }
            catch (Exception exp)
            {
                Log.Error(string.Format("Exception calculating predicted discharge from '{0}', '{1}', '{2}, applyShifts = '{3}'': ",
                  ratingModelIdentifier, stage, measurementTime, applyShifts), exp);
            }

            return predictedDischarge;
        }

        public List<FieldVisitDescription> GetFieldVisitDescriptions(string locIdentifier, DateTimeOffset? queryFrom, DateTimeOffset? queryTo)
        {
            FieldVisitDescriptionListServiceRequest request = new FieldVisitDescriptionListServiceRequest();
            request.LocationIdentifier = locIdentifier;
            request.QueryFrom = queryFrom;
            request.QueryTo = queryTo;
            request.IncludeInvalidFieldVisits = false;

            FieldVisitDescriptionListServiceResponse response = Publish().Get(request);

            return response.FieldVisitDescriptions;
        }

        public FieldVisitDataServiceResponse GetFieldVisitData(FieldVisitDescription fieldVisitDescription)
        {
            FieldVisitDataServiceRequest request = new FieldVisitDataServiceRequest();
            request.FieldVisitIdentifier = fieldVisitDescription.Identifier;
            request.ApplyRounding = true;
            request.IncludeInvalidActivities = false;

            FieldVisitDataServiceResponse response = Publish().Get(request);
            return response;
        }

        public List<FieldVisit> GetFieldVisitDataByLocation(string locationIdentifier, IEnumerable<ActivityType> activities = null, IEnumerable<string> parameters = null)
        {
            var request = new FieldVisitDataByLocationServiceRequest
            {
                LocationIdentifier = locationIdentifier,
                Activities = activities?.ToList() ?? new List<ActivityType> { ActivityType.Reading },
                Parameters = parameters?.ToList(),
            };

            return Publish().Get(request).FieldVisitData;
        }

        public List<Reading> GetFieldVisitReadingsByLocation(string locationIdentifier, IEnumerable<string> parameters = null)
        {
            return GetFieldVisitDataByLocation(locationIdentifier, new [] { ActivityType.Reading }, parameters)
                .Where(fv => fv.InspectionActivity?.Readings != null)
                .SelectMany(fv => fv.InspectionActivity.Readings)
                .ToList();
        }

        public List<string> GetFilterList(string filterString)
        {
            List<string> filterList = new List<string>();

            if (!string.IsNullOrEmpty(filterString))
            {
                string[] filterArray = filterString.Split(new string[] { "+" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string filter in filterArray) filterList.Add(filter.Trim());
            }
            return filterList;
        }
        public List<LocationDescription> GetLocationDescriptions(string locationIdentifierFilter, string locationNameFilter,
            string primaryFolderFilter, List<string> locationTagKeysFilterList, List<string> locationTagValuesFilterList)
        {
            List<LocationDescription> locationDescriptions = new List<LocationDescription>();
            try
            {
                LocationDescriptionListServiceRequest locationDescriptionRequest = new LocationDescriptionListServiceRequest();

                if (!string.IsNullOrEmpty(locationIdentifierFilter)) locationDescriptionRequest.LocationIdentifier = locationIdentifierFilter;
                if (!string.IsNullOrEmpty(locationNameFilter)) locationDescriptionRequest.LocationName = locationNameFilter;
                if (!string.IsNullOrEmpty(primaryFolderFilter)) locationDescriptionRequest.LocationFolder = primaryFolderFilter;
                if (locationTagKeysFilterList.Count > 0) locationDescriptionRequest.TagKeys = locationTagKeysFilterList;
                if (locationTagValuesFilterList.Count > 0) locationDescriptionRequest.TagValues = locationTagValuesFilterList;

                locationDescriptions = Publish().Get(locationDescriptionRequest).LocationDescriptions;
            }
            catch { }

            return locationDescriptions;
        }

        public List<LocationDescription> GetLocationDescriptions(string locationIdentifierFilter, string locationNameFilter,
            string primaryFolderFilter, List<string> locationTagFilterList)
        {
            List<LocationDescription> locationDescriptions = new List<LocationDescription>();
            try
            {
                List<TagDefinition> tags = GetTagDefinitions("Location");

                List<string> keysFilter = new List<string>();
                List<string> valuesFilter = new List<string>();
                List<string> pairsFilter = new List<string>();

                bool unknownTagFilterItem = false;
                foreach (string tagFilter in locationTagFilterList)
                {
                    bool matchesKey = MatchesATagKey(tagFilter, tags);
                    if (matchesKey) keysFilter.Add(tagFilter);

                    bool matchesValue = MatchesATagValue(tagFilter, tags);
                    if (matchesValue) valuesFilter.Add(tagFilter);

                    if (!matchesKey && !matchesValue)
                    {
                        string pairString = MatchesATagPair(tagFilter, tags);
                        if (!string.IsNullOrEmpty(pairString))
                        {
                            pairsFilter.Add(pairString);

                            string[] keyValuePair = pairString.Split(new string[] { "+" }, StringSplitOptions.None);
                            keysFilter.Add(keyValuePair[0]);
                            valuesFilter.Add(keyValuePair[1]);
                        }
                        else
                        {
                            Log.InfoFormat("Location Tag Filter unknown item: '{0}'", tagFilter);
                            unknownTagFilterItem = true;
                        };
                    }
                }

                LogTagFilters("Location", locationTagFilterList, keysFilter, valuesFilter, pairsFilter);

                if (!unknownTagFilterItem)
                {
                    LocationDescriptionListServiceRequest locationDescriptionRequest = new LocationDescriptionListServiceRequest();

                    if (!string.IsNullOrEmpty(locationIdentifierFilter)) locationDescriptionRequest.LocationIdentifier = locationIdentifierFilter;
                    if (!string.IsNullOrEmpty(locationNameFilter)) locationDescriptionRequest.LocationName = locationNameFilter;
                    if (!string.IsNullOrEmpty(primaryFolderFilter)) locationDescriptionRequest.LocationFolder = primaryFolderFilter;
                    if (keysFilter.Count > 0) locationDescriptionRequest.TagKeys = keysFilter;
                    if (valuesFilter.Count > 0) locationDescriptionRequest.TagValues = valuesFilter;

                    LocationDescriptionListServiceResponse locResponse = Publish().Get(locationDescriptionRequest);

                    if (pairsFilter.Count == 0)
                    {
                        locationDescriptions = locResponse.LocationDescriptions;
                    }
                    else
                    {
                        int pairsCount = pairsFilter.Count;
                        foreach (LocationDescription locDesc in locResponse.LocationDescriptions)
                        {
                            int matchCount = 0;
                            foreach (string pairFilter in pairsFilter)
                            {
                                string[] keyValuePairs = pairFilter.Split(new string[] { "+" }, StringSplitOptions.None);
                                foreach (TagMetadata tag in locDesc.Tags)
                                {
                                    if (!string.IsNullOrEmpty(tag.Value) &&
                                        MatchPartialNameFilter(keyValuePairs[0], tag.Key) &&
                                        MatchPartialNameFilter(keyValuePairs[1], tag.Value))
                                        matchCount++;
                                }
                            }
                            if (matchCount == pairsCount) locationDescriptions.Add(locDesc);
                        }
                    }
                }
            }
            catch { }

            return locationDescriptions;
        }

        public bool MatchesATagKey(string tagFilter, List<TagDefinition> Tags)
        {
            string matchThisString = "^" + tagFilter.Replace("*", ".*") + "$";
            Regex regex = new Regex(matchThisString, RegexOptions.IgnoreCase);
            foreach (TagDefinition tag in Tags)
            {
                if (regex.Match(tag.Key).Success)
                    return true;
            }         
            return false;
        }

        public bool MatchesATagValue(string tagFilter, List<TagDefinition> Tags)
        {
            string matchThisString = "^" + tagFilter.Replace("*", ".*") + "$";
            Regex regex = new Regex(matchThisString, RegexOptions.IgnoreCase);
            foreach (TagDefinition tag in Tags)
            {
                if (!tag.ValueType.HasValue) continue;
                if (tag.ValueType.Value != TagValueType.PickList) continue;

                foreach (string pickListValue in tag.PickListValues)
                    if (regex.Match(pickListValue).Success)
                        return true;
            }
            return false;
        }

        public string MatchesATagPair(string tagFilter, List<TagDefinition> Tags)
        {
            if (!tagFilter.Contains(":")) return null;

            string[] splitItems = tagFilter.Split(new string[] { ":" }, StringSplitOptions.None);

            for (int i = 0; i < splitItems.Length - 1; i++)
            {
                string leftSubstring = splitItems[0];
                for (int j = 1; j < i + 1; j++) leftSubstring += ":" + splitItems[j];
                string rightSubstring = splitItems[i + 1];
                for (int j = i + 2; j < splitItems.Length; j++) rightSubstring += ":" + splitItems[j];

                leftSubstring = leftSubstring.Trim();
                rightSubstring = rightSubstring.Trim();

                string leftSubstringRegEx = "^" + leftSubstring.Replace("*", ".*") + "$";
                Regex leftRegex = new Regex(leftSubstringRegEx, RegexOptions.IgnoreCase);
                string rightSubstringRegEx = "^" + rightSubstring.Replace("*", ".*") + "$";
                Regex rightRegex = new Regex(rightSubstringRegEx, RegexOptions.IgnoreCase);

                foreach (TagDefinition tag in Tags)
                {
                    if (!tag.ValueType.HasValue) continue;
                    if (tag.ValueType.Value != TagValueType.PickList) continue;

                    if (!leftRegex.Match(tag.Key).Success) continue;

                    foreach (string pickListValue in tag.PickListValues)
                        if (rightRegex.Match(pickListValue).Success)
                            return string.Format("{0}+{1}", leftSubstring, rightSubstring);
                }
            }
            return null;
        }

        public List<string> GetUnknownTagFilters(List<string> tagsFilterList, string tagType)
        {
            List<string> unknownFilters = new List<string>();
            List<TagDefinition> tags = GetTagDefinitions(tagType);

            foreach (string tagFilter in tagsFilterList)
            {
                bool matchesKey = MatchesATagKey(tagFilter, tags);
                bool matchesValue = MatchesATagValue(tagFilter, tags);

                if (!matchesKey && !matchesValue && string.IsNullOrEmpty(MatchesATagPair(tagFilter, tags)))
                    unknownFilters.Add(tagFilter);
            }
            return unknownFilters;
        }

        public string GetUnknownTagFilterMessage(List<string> tagFilterList, string tagType)
        {
            List<string> unknownTags = GetUnknownTagFilters(tagFilterList, tagType);
            if (unknownTags.Count > 0)
            {
                return string.Format("{0} '{1}'",
                    ((unknownTags.Count > 1) ?
                    Common.GetLocalizedDisplayString("Unknown " + tagType + " Tag Filter Items") :
                    Common.GetLocalizedDisplayString("Unknown " + tagType + " Tag Filter Item")),
                    String.Join(", ", unknownTags));
            }
            return "";
        }

        public List<TagDefinition> GetTagDefinitions()
        {
            return Publish().Get(new TagListServiceRequest()).Tags;
        }

        public List<TagDefinition> GetTagDefinitions(string tagType)
        {
            List<TagDefinition> allTagDefinitions = GetTagDefinitions();
            List<TagDefinition> filteredTagDefinitions = new List<TagDefinition>();
            foreach (TagDefinition tag in allTagDefinitions)
            {
                if ((tagType == "Location") && !tag.AppliesToLocations) continue;
                if ((tagType == "LocationNote") && !tag.AppliesToLocationNotes) continue;
                if ((tagType == "SensorsGauges") && !tag.AppliesToSensorsGauges) continue;

                filteredTagDefinitions.Add(tag);
            }
            return filteredTagDefinitions;
        }
        public static bool MaximumTimeExceeded(DateTimeOffset reportStartTime, double maximumTimeInMinutes, string outputFormat)
        {
            double timeForReportGeneration = 0.5;

            if (maximumTimeInMinutes < 0.0) return false;

            TimeSpan elapsedTime = DateTimeOffset.Now - reportStartTime;
            if (elapsedTime.TotalMinutes + timeForReportGeneration > maximumTimeInMinutes)
                return true;

            return false;
        }
        public static string GetTableTruncatedInformationString(double timeLimitInMinutes)
        {
            return string.Format(Resources.TheTableHasBeenTruncatedX, timeLimitInMinutes);
        }

        public static void LogTagFilters(string tagType, List<string> tagFilterList, 
            List<string> keysFilter, List<string> valuesFilter, List<string> pairsFilter)
        {
            Log.DebugFormat("{0} Tag Filter List: '{1}'", tagType, String.Join(", ", tagFilterList));
            Log.DebugFormat("{0} Tag Filter Keys: '{1}'", tagType, String.Join(", ", keysFilter));
            Log.DebugFormat("{0} Tag Filter Values: '{1}'", tagType, String.Join(", ", valuesFilter));

            foreach (string pairFilter in pairsFilter)
            {
                string[] pair = pairFilter.Split(new string[] { "+" }, StringSplitOptions.None);
                Log.DebugFormat("{0} Tag Filter Pair: key='{1}' and value='{2}'", tagType, pair[0], pair[1]);
            }
        }
    }
}
