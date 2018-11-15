﻿using ReportPluginFramework.Beta;

namespace Reports
{
    public class DiscreteData : DiscreteDataNamespace.ReportPluginBase, IReport
    {
        public override void AddReportSpecificTables(System.Data.DataSet dataSet)
        {
            DiscreteDataNamespace.ReportSpecificTablesBuilder.AddReportSpecificTables(dataSet);
        }
    }
}