﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Net;

namespace ContinuumNS
{
    /// <summary>
    /// Class that executes lengthy processes on a separate (background) thread so that the GUI doesn't appear unresponsive.
    /// Background tasks include: 
    /// 1) Topography and Land Cover import
    /// 2) MERRA2 data download and extracting
    /// 3) Met site calcs and model creation
    /// 4) Turbine site calcs and estimates
    /// 5) Round Robin analysis
    /// 6) Map and WRG file creation
    /// 7) Exceedance Monte Carlo analysis
    /// 8) Shadow flicker model
    /// 9) Ice throw model
    /// 10) Save As file and node SRDH recalculation (if land cover key changed)
    /// </summary>
    public partial class BackgroundWork : Form
    {
        Update updateThe = new Update();
        /// <summary> Set to true when task is finished. Checked during unit tests </summary>
        public bool DoWorkDone = false;
        /// <summary> Set to true if Background worker did not finish and was cancelled  </summary>
        public bool WasReturned = false;

        /// <summary> Class initializer </summary>
        public BackgroundWork()
        {
            InitializeComponent();
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////////

        /// <summary> Struct containing Continuum instance and filename </summary>
        public struct Vars_for_BW
        {
            /// <summary> Continuum instance </summary>
            public Continuum thisInst;
            /// <summary> File to be imported </summary>
            public string Filename;
        }

        /// <summary> Struct containg info needed to create WRG file </summary>
        public struct Vars_for_WRG_file
        {
            /// <summary> Continuum instance </summary>
            public Continuum thisInst;
            /// <summary> Map used to generate WRG file </summary>
            public string mapName;
            /// <summary> User-entered mean air density </summary>
            public double density;
            /// <summary> WRG filename </summary>
            public string outputFile;
        }

        /// <summary> Struct containing new/old filename used in 'Save As' </summary>
        public struct Vars_for_Save_As
        {
            /// <summary> Old Continuum filename </summary>
            public string oldFilename;
            /// <summary> New Continuum filename </summary>
            public string newFilename;
        }

        /// <summary> Contains Continuum instance and Map object </summary>
        public struct Vars_for_Gen_Map
        {
            /// <summary> Continuum instance </summary>
            public Continuum thisInst;
            /// <summary> Map to be generated </summary>
            public Map thisMap;
            /// <summary> Selected MCP method </summary>
            public string MCP_Method;
        }

        /// <summary> Contains Continuum instance and wake model </summary>
        public struct Vars_for_TurbCalcs
        {
            /// <summary> Continuum instance </summary>
            public Continuum thisInst;
            /// <summary> Wake model to use in net calcs </summary>
            public Wake_Model thisWakeModel;
            /// <summary> Selected MCP method </summary>
            public string MCP_Method;
        }

        /// <summary> Contains Continuum instance and selected subset size </summary>
        public struct Vars_for_RoundRobin
        {
            /// <summary> Round Robin met subset size </summary>
            public int Min_RR_Size;
            /// <summary> Continuum instance </summary>
            public Continuum thisInst;
            /// <summary> Selected MCP method </summary>
            public string MCP_Method;
        }               

        /// <summary> Contains MERRA2 object to extract from local files </summary>
        public struct Vars_for_MERRA
        {
            /// <summary> MERRA2 object to fill with data </summary>
            public MERRA thisMERRA;
            /// <summary> MERRA2 nodes to pull from local files </summary>
            public MERRA.MERRA_Pull[] nodesToPull;
            /// <summary> Continuum instance </summary>
            public Continuum thisInst;
            /// <summary> Selected MCP method </summary>
            public string MCP_type;
            /// <summary> Selected met (if any) to conduct MCP </summary>
            public Met thisMet;
        }          
        
        //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        /// <summary> Calls Met Calcs background worker and runs background task </summary>        
        public void Call_BW_MetCalcs(Vars_for_BW theArgs)
        {
            // Calls Met Calcs background worker
            Show();
            BackgroundWorker_MetCalcs.RunWorkerAsync(theArgs);
        }

        private void BackgroundWorker_MetCalcs_DoWork(object sender, DoWorkEventArgs e)
        {
            // For each met, calculates exposure and grid stats, create met pairs and perform WS cross-prediction, finds site-calibrated model (if >1 met)
            // updates turbine calculations (if any)
            DoWorkDone = false;
            WasReturned = false;
            Vars_for_BW theArgs = (Vars_for_BW)e.Argument;
            Continuum thisInst = theArgs.thisInst;
     
            MetCollection metList = thisInst.metList;
            TopoInfo topo = thisInst.topo;      
            MetPairCollection metPairs = thisInst.metPairList;
            ModelCollection modelList = thisInst.modelList;
            NodeCollection nodeList = new NodeCollection();                 

            string textForProgBar = "Importing Met Sites";
            BackgroundWorker_MetCalcs.ReportProgress(0, textForProgBar);
            int numMets = metList.ThisCount;
            int numRadii = thisInst.radiiList.ThisCount;

            textForProgBar = "Calculating exposures at met sites.";
            BackgroundWorker_MetCalcs.ReportProgress(0, textForProgBar);

            // Get elevs for calcs
            topo.GetElevsAndSRDH_ForCalcs(thisInst, null, false);

            // Do Exposure calcs at new met site(s) and update bulk UW and DW expos at all other mets
            for (int m = 0; m < numMets; m++)
            {
                for (int r = 0; r < numRadii; r++)
                {
                    if (BackgroundWorker_MetCalcs.CancellationPending == true)
                    {
                        e.Cancel = true;
                        WasReturned = true;
                        return;
                    }

                    metList.CalcMetExposures(m, r, thisInst);
                }
            }

            metList.expoIsCalc = true;

            if (topo.gotSR == true)
                metList.SRDH_IsCalc = true;

            textForProgBar = "Finished exposure calculations at met sites.";
            BackgroundWorker_MetCalcs.ReportProgress(10, textForProgBar);

            for (int i = 0; i < numMets; i++)
            {
                if (BackgroundWorker_MetCalcs.CancellationPending == true)
                {
                    e.Cancel = true;
                    WasReturned = true;
                    return;
                }

                Met thisMet = metList.metItem[i];

                if (thisMet.gridStats.stats == null)
                    thisMet.gridStats.GetGridArrayAndCalcStats(thisMet.UTMX, thisMet.UTMY, thisInst);

                // Find flow separation nodes for met site
                if (topo.useSepMod == true) thisMet.GetFlowSepNodes(nodeList, thisInst);

                textForProgBar = "Finished grid stat calculations at " + (i + 1).ToString() + "/" + numMets + " met sites.";
                BackgroundWorker_MetCalcs.ReportProgress(10 + 40 * (i + 1) / numMets, textForProgBar);
            }

            // If using time series mets and isMCPd = true, only create model if all mets have LT ests (i.e. user might have to import MERRA data)
            thisInst.metList.AreAllMetsMCPd();
            if (thisInst.metList.isTimeSeries && thisInst.metList.isMCPd && thisInst.metList.allMCPd == false)
            {
                e.Cancel = true;
                WasReturned = true;
                return;
            }

            // Create met pairs with single met models at all radii in list.  First checks to see what pairs already exist before making new ones.
            metPairs.CreateMetPairs(thisInst);
            int numPairs = metPairs.PairCount;                       
                  
            // if imported coeffs are used, don't do site-calibration
            int importedInd = modelList.GetImportedModelInd();
            int Keep_Imported = 0;

            if (importedInd >= 0)
            {
                if (numPairs > 0)
                    Keep_Imported = (int)MessageBox.Show("Model coefficients have been imported for this analysis. Do you wish to keep this model or create a new site-calibrated model" +
                        " with the mets being imported? Click Yes to keep imported model. Click No to delete imported model and create site-calibrated model.", "Continuum 3", MessageBoxButtons.YesNo);

                if (Keep_Imported == (int)DialogResult.No)
                    modelList.ClearImported();

            }
            else if (importedInd == -1)
                Keep_Imported = 0;

            if (Keep_Imported == 0)
            {
                textForProgBar = "Finding site-calibrated models...";
                BackgroundWorker_MetCalcs.ReportProgress(0, textForProgBar);
                modelList.ClearImported();
                if (metList.isTimeSeries == false || metList.isMCPd == false)
                {
                    BackgroundWorker_MetCalcs.ReportProgress(50, textForProgBar);
                    if (thisInst.metList.ThisCount == 1)
                        modelList.CreateDefaultModels(thisInst);
                    else
                        modelList.FindSiteCalibratedModels(thisInst, Met.TOD.All, Met.Season.All, thisInst.modeledHeight);
                }
                else // seasonal and diurnal models created using long-term estimated time series
                {
                    if (thisInst.metList.ThisCount == 1)
                    {
                        modelList.CreateDefaultModels(thisInst);
                    }
                    else
                    {
                        // Create a model using all hours and all seasons
                        modelList.FindSiteCalibratedModels(thisInst, Met.TOD.All, Met.Season.All, thisInst.modeledHeight);

                        if ((thisInst.metList.numTOD != 1) || (thisInst.metList.numSeason != 1)) // uses day/night and/or seasonal models
                        {
                            if (thisInst.metList.numTOD > 1 && thisInst.metList.numSeason == 1) // use day/night model, not seasonal
                            {
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Day, Met.Season.All, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(50, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Night, Met.Season.All, thisInst.modeledHeight);
                            }
                            else if (thisInst.metList.numTOD == 1 && thisInst.metList.numSeason > 1) // uses seasonal models but not day/night
                            {
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.All, Met.Season.Winter, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(25, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.All, Met.Season.Spring, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(50, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.All, Met.Season.Summer, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(75, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.All, Met.Season.Fall, thisInst.modeledHeight);
                            }
                            else // uses seasonal and diurnal models
                            {
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Day, Met.Season.Winter, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(12, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Night, Met.Season.Winter, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(24, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Day, Met.Season.Spring, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(36, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Night, Met.Season.Spring, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(48, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Day, Met.Season.Summer, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(60, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Night, Met.Season.Summer, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(72, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Day, Met.Season.Fall, thisInst.modeledHeight);
                                BackgroundWorker_MetCalcs.ReportProgress(84, textForProgBar);
                                modelList.FindSiteCalibratedModels(thisInst, Met.TOD.Night, Met.Season.Fall, thisInst.modeledHeight);
                            }
                        }
                    }
                }
            }

            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_MetCalcs_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the met calculation progress bar
            BringToFront();
            string textForLabel = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = textForLabel;
            Refresh();
        }

        private void BackgroundWorker_MetCalcs_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates all met lists, turbine calcs, model plots etc on main form 
            if (e.Cancelled == false)
            {
                Continuum thisInst = (Continuum)e.Result;

                if (thisInst.IsDisposed == false)
                {
                    thisInst.SaveFile(false);
                    updateThe.AllTABs(thisInst);
                }               

            }

            Close();
        }

        /// <summary> Calls topography file import background worker and runs background task </summary>        
        public void Call_BW_TopoImport(Vars_for_BW Args)
        {            
            Show();
            BackgroundWorker_Topo.RunWorkerAsync(Args);
        }

        private void BackgroundWorker_Topo_DoWork(object sender, DoWorkEventArgs e)
        {
            // Opens and reads topography data from a .XYZ, .TIF, or .ADF file. Saves elevation data to local database.
            NodeCollection nodeList = new NodeCollection();            
            Vars_for_BW args = (Vars_for_BW)e.Argument;
            Continuum thisInst = args.thisInst;
            DoWorkDone = false;
            WasReturned = false;

            string filename = args.Filename;
            TopoInfo topo = thisInst.topo;
            string savedFilename = thisInst.savedParams.savedFileName;
            thisInst.savedParams.topoFileName = filename;

            string connString = nodeList.GetDB_ConnectionString(savedFilename);
            string textForProgBar = "Reading in topography...";
            BackgroundWorker_Topo.ReportProgress(10, textForProgBar);

            bool isTIF = false;
            bool isSHP = false;
            bool isADF = false;

            string thisExt = filename.Substring(filename.Length - 3);

            if (thisExt == "TIF" || thisExt == "tif")
                isTIF = true;
            else if (thisExt == "SHP" || thisExt == "shp")
                isSHP = true;
            else if (thisExt == "ADF" || thisExt == "adf")
                isADF = true;

            thisInst.updateThe.Clear_Topo_DB(thisInst.savedParams.savedFileName);

            if (isTIF == true || isSHP == true || isADF == true)
            {
                bool goodToGo = topo.ReadGeoTiffTopo(filename, thisInst);

                if (goodToGo == false)
                {
                    e.Result = thisInst;
                    WasReturned = true; // for unit tests
                    return;
                }

                double[,] topoForDB = topo.topoElevs;
                topo.DecimateForPlot("topo");

                Check_class checker = new Check_class();
                // Checks elev at 8 points around each met/turbine +/-12000 m and return false if elev = -999 or if mets out of range
                goodToGo = checker.NewTopo(topo, thisInst.metList, thisInst.turbineList);

                if (topo.topoElevs == null || goodToGo == false)
                {
                    e.Result = thisInst;
                    WasReturned = true; // for unit tests
                    return;
                }

                int thisCount = 0;
                int numAll = topo.topoNumXY.X.all.num * topo.topoNumXY.Y.all.num;
                Topo_table topoEntry = new Topo_table();

                float[] elevsAlongX = new float[topo.topoNumXY.Y.all.num];
                int entryInd = 0;
                BinaryFormatter bin = new BinaryFormatter();

                using (var ctx = new Continuum_EDMContainer(connString))
                {
                    for (int i = 0; i <= topoForDB.GetUpperBound(0); i++)
                    {
                        for (int j = 0; j <= topoForDB.GetUpperBound(1); j++)
                        {
                            thisCount++;

                            elevsAlongX[entryInd] = (float)topoForDB[i, j];
                            entryInd++;

                            if (entryInd == topo.topoNumXY.Y.all.num)
                            {
                                textForProgBar = "Reading in topography...";
                                int Prog = Convert.ToInt16(100 * ((double)thisCount / (numAll - 1)));
                                BackgroundWorker_Topo.ReportProgress(Prog, textForProgBar);

                                MemoryStream MS2 = new MemoryStream();
                                bin.Serialize(MS2, elevsAlongX);
                                topoEntry.Elevs = MS2.ToArray();

                                ctx.Topo_table.Add(topoEntry);
                                ctx.SaveChanges();
                                entryInd = 0;
                                elevsAlongX = new float[topo.topoNumXY.Y.all.num];
                            }


                            if (BackgroundWorker_Topo.CancellationPending == true)
                            {
                                thisInst.topo.topoElevs = null;
                                updateThe.Clear_Topo_DB(thisInst.savedParams.savedFileName);
                                e.Cancel = true;
                                WasReturned = true; // for unit tests
                                return;
                            }
                        }
                    }

                    ctx.SaveChanges();
                    ctx.Database.Connection.Close();
                }
            }

            topo.gotTopo = true;
            
            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_Topo_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the topo data import progress bar
            string Text_for_label = e.UserState.ToString();

            if (e.ProgressPercentage <= 100)
                progbar.Value = e.ProgressPercentage;
            else
                progbar.Value = 100;

            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            Refresh();
        }

        private void BackgroundWorker_Topo_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates the topography contour map and map on Advanced tab and saves file                        
            if (e.Cancelled == false)
            {
                Continuum thisInst = (Continuum)e.Result;

                if (thisInst.IsDisposed == false)
                {
                    thisInst.SaveFile(false);
                    updateThe.AllTABs((Continuum)e.Result);
                }
                
            }

            Close();
        }

        /// <summary> Calls land cover file import background worker and runs background task      /// </summary>
        /// <param name="Args"></param>
        public void Call_BW_LandCoverImport(Vars_for_BW Args)
        {            
            Show();
            BackgroundWorker_LandCover.RunWorkerAsync(Args);
        }

        private void BackgroundWorker_LandCover_DoWork(object sender, DoWorkEventArgs e)
        {
            // Opens and reads in land cover data as .TIF file, calculate SRDH at mets and turbine sites

            Vars_for_BW The_args = (Vars_for_BW)e.Argument;
            Continuum thisInst = The_args.thisInst;
            TopoInfo topo = thisInst.topo;
            string savedFilename = thisInst.savedParams.savedFileName;
            string wholePath = The_args.Filename;
            MetCollection metList = thisInst.metList;
            TurbineCollection turbList = thisInst.turbineList;
            NodeCollection nodeList = new NodeCollection();
            thisInst.savedParams.landCoverFileName = wholePath;

            DoWorkDone = false;
            WasReturned = false;

            string textForProgBar = "Reading in land cover data...";
            BackgroundWorker_LandCover.ReportProgress(10, textForProgBar);

            if (topo.LC_Key == null)
                topo.SetUS_NLCD_Key();

            bool goodToGo = topo.ReadGeoTiffLandCover(wholePath, thisInst);

            if (topo.landCover == null || goodToGo == false)
            {
                e.Result = thisInst;
                WasReturned = true; // for unit tests
                return;
            }

            int[,] landCoverForDB = topo.landCover;
            topo.DecimateForPlot("LC");
            Check_class checker = new Check_class();
            bool Input_LC = checker.NewLandCover(topo, metList, turbList);  // Check that all existing mets and turbines fall within bounds of new land cover data

            if (Input_LC == false)
            {
                e.Result = thisInst;
                WasReturned = true; // for unit tests
                return;
            }

            int thisCount = 0;
            int numAll = topo.LC_NumXY.X.all.num * topo.LC_NumXY.Y.all.num;
            LandCover_table LC_Entry = new LandCover_table();

            int[] LCsAlongX = new int[topo.LC_NumXY.Y.all.num];
            int entryInd = 0;
            BinaryFormatter bin = new BinaryFormatter();
            string connString = nodeList.GetDB_ConnectionString(savedFilename);

            using (var ctx = new Continuum_EDMContainer(connString))
            {
                for (int i = 0; i <= landCoverForDB.GetUpperBound(0); i++)
                {
                    for (int j = 0; j <= landCoverForDB.GetUpperBound(1); j++)
                    {
                        thisCount++;

                        LCsAlongX[entryInd] = landCoverForDB[i, j];
                        entryInd++;

                        if (entryInd == topo.LC_NumXY.Y.all.num)
                        {
                            textForProgBar = "Reading in land cover...";
                            int prog = Convert.ToInt16(100 * ((double)thisCount / (numAll - 1)));
                            BackgroundWorker_LandCover.ReportProgress(prog, textForProgBar);

                            MemoryStream MS2 = new MemoryStream();
                            bin.Serialize(MS2, LCsAlongX);
                            LC_Entry.LandCover = MS2.ToArray();

                            ctx.LandCover_table.Add(LC_Entry);
                            ctx.SaveChanges();
                            entryInd = 0;
                            LCsAlongX = new int[topo.LC_NumXY.Y.all.num];
                        }

                        if (BackgroundWorker_LandCover.CancellationPending == true)
                        {
                            thisInst.topo.gotSR = false;
                            updateThe.Clear_LandCover_DB(thisInst.savedParams.savedFileName);
                            e.Result = thisInst;
                            WasReturned = true; // for unit tests
                            return;
                        }
                    }
                }

                ctx.SaveChanges();
                ctx.Database.Connection.Close();
            }

            topo.gotSR = true;

            // Calculate SR/DH at mets, turbines, and nodes in database
            thisInst.topo.GetElevsAndSRDH_ForCalcs(thisInst, null, true);
            metList.ReCalcSRDH(thisInst.topo, thisInst.radiiList);

            turbList.ReCalcTurbine_SRDH(thisInst);

            // Recalculates SR/DH with new land cover key (or calculates SR/DH with newly imported land cover)
            Nodes nodeFromDB = new Nodes();
            int nodeCount = 0;

            try
            {
                using (var context = new Continuum_EDMContainer(connString))
                {
                    var node_db = from N in context.Node_table.Include("expo") select N;
                    int totalCount = node_db.Count();

                    foreach (var N in node_db)
                    {
                        int numExpo = N.expo.Count();
                        nodeFromDB.UTMX = N.UTMX;
                        nodeFromDB.UTMY = N.UTMY;

                        nodeFromDB.expo = new Exposure[numExpo];

                        if (nodeCount % 10 == 0)
                        {
                            textForProgBar = "Recalculating SR/DH at Nodes in database. Node: " + nodeCount + "/" + totalCount;
                            BackgroundWorker_LandCover.ReportProgress(100 * nodeCount / totalCount, textForProgBar);
                        }

                        for (int i = 0; i <= numExpo - 1; i++)
                        {
                            nodeFromDB.expo[i] = new Exposure();
                            nodeFromDB.expo[i].radius = N.expo.ElementAt(i).radius;
                            nodeFromDB.expo[i].exponent = N.expo.ElementAt(i).exponent;
                            int numSectors = 1;

                            // Recalc SR / DH                            
                            int smallerRadius = thisInst.topo.GetSmallerRadius(nodeFromDB.expo, nodeFromDB.expo[i].radius, nodeFromDB.expo[i].exponent, numSectors);

                            if (smallerRadius == 0 || numSectors > 1)
                            { // when sector avg is used, can//t add on to exposure calcs...so gotta do it the long way
                                thisInst.topo.CalcSRDH(ref nodeFromDB.expo[i], nodeFromDB.UTMX, nodeFromDB.UTMY, nodeFromDB.expo[i].radius, nodeFromDB.expo[i].exponent, thisInst.metList.numWD);
                            }
                            else
                            {
                                Exposure smallerExpo = thisInst.topo.GetSmallerRadiusExpo(nodeFromDB.expo, smallerRadius, nodeFromDB.expo[i].exponent, numSectors);
                                thisInst.topo.CalcSRDHwithSmallerRadius(ref nodeFromDB.expo[i], nodeFromDB.UTMX, nodeFromDB.UTMY, nodeFromDB.expo[i].radius, nodeFromDB.expo[i].exponent, numSectors, smallerRadius, smallerExpo, thisInst.metList.numWD);
                            }

                            // Write back to expo_db
                            MemoryStream MS7 = new MemoryStream();
                            MemoryStream MS8 = new MemoryStream();

                            if (nodeFromDB.expo[i].SR != null)
                            {
                                bin.Serialize(MS7, nodeFromDB.expo[i].SR);
                                N.expo.ElementAt(i).SR_Array = MS7.ToArray();
                            }

                            if (nodeFromDB.expo[i].dispH != null)
                            {
                                bin.Serialize(MS8, nodeFromDB.expo[i].dispH);
                                N.expo.ElementAt(i).DH_Array = MS8.ToArray();
                            }
                        }

                        nodeCount++;
                    }

                    // Save DB
                    context.SaveChanges();
                    context.Database.Connection.Close();
                }
                
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.InnerException.ToString());
                WasReturned = true; // for unit tests
                return;
            }                       
           
            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_LandCover_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Update progress bar for land cover data import
            string Text_for_label = e.UserState.ToString();
            if (e.ProgressPercentage <= 100)
                progbar.Value = e.ProgressPercentage;
            else
                progbar.Value = 100;

            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            Refresh();
        }

        private void BackgroundWorker_LandCover_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates met and turbine SRDH plots and tables, topo map, Advanced tab after land cover data import
            Continuum thisInst = (Continuum)e.Result;

            if (thisInst.IsDisposed == false)
            {
                if (e.Cancelled == false)
                    thisInst.SaveFile(false);

                updateThe.AllTABs(thisInst);
            }
            
            Close();
        }

        /// <summary> Calls .MAP roughness file import background worker and runs background task </summary>        
        public void Call_BW_WAsP_Map(Vars_for_BW Args)
        {            
            Show();
            BackgroundWorker_WAsP_Map.RunWorkerAsync(Args);
        }

        private void BackgroundWorker_WAsP_Map_DoWork(object sender, DoWorkEventArgs e)
        {
            // Reads in a .MAP surface roughness contour file and creates the LandCover map (to do: import shape files)
            Vars_for_BW theArgs = (Vars_for_BW)e.Argument;
            Continuum thisInst = theArgs.thisInst;
            string fileName = theArgs.Filename;
            TopoInfo topo = thisInst.topo;
            MetCollection metList = thisInst.metList;
            TurbineCollection turbList = thisInst.turbineList;
            InvestCollection radiiList = thisInst.radiiList;
            string savedFilename = thisInst.savedParams.savedFileName;
            NodeCollection nodeList = new NodeCollection();

        /*    bool Is_TIF = false;
            bool Is_SHP = false;

            if (fileName.Substring(fileName.Length - 3) == "TIF" || fileName.Substring(fileName.Length - 3) == "tif")
                Is_TIF = true;
            else if (fileName.Substring(fileName.Length - 3) == "SHP" || fileName.Substring(fileName.Length - 3) == "shp")
                Is_SHP = true;

            if (Is_SHP == true)
            {
                UTM_conversion UTM = new UTM_conversion();
                topo.Read_SHP_Roughness(fileName, UTM); //// this doesn;t work yet
            }
*/

            StreamReader sr = new StreamReader(fileName);

            for (int i = 0; i <= 3; i++)
                sr.ReadLine();

            TopoInfo.Roughness_Map_Struct[] theseShapes = null;
            int shapeCount = 0;
            
            string textForProgBar = "";
            BackgroundWorker_WAsP_Map.ReportProgress(20, textForProgBar);

            topo.LC_NumXY.X.all.min = 10000000;
            topo.LC_NumXY.X.all.max = 0;
            topo.LC_NumXY.Y.all.min = 10000000;
            topo.LC_NumXY.Y.all.max = 0;

            // Read file and save all shapes in These_shapes() array
            // Roughness_Map_Struct has: minX/Y, maxX/Y, Left_Rough, Right_Rough, numPoints, Points()  UTM_X_Y, Is_Closed as bool
            // Define Min/Max_UTM_LC based on Min/Max X/Y of shapes

            // Read file and create array of Roughness_Map_Struct objects: These_shapes()
            // Also find min/max X/Y of all shapes to determine extent and dimensions of landCover array

            while (sr.EndOfStream == false)
            {
                Array.Resize(ref theseShapes, shapeCount + 1);
                theseShapes[shapeCount] = new TopoInfo.Roughness_Map_Struct();

                string thisDataString = sr.ReadLine();
                string[] dataSplit = thisDataString.Split(' ');

                string[] trimmedDataSplit = new string[0];
                int dataCount = 0;

                if (dataSplit == null)
                {
                    MessageBox.Show("Error importing .MAP file.");
                    e.Result = thisInst;
                    return;
                }

                for (int i = 0; i < dataSplit.Length; i++)
                {
                    if (dataSplit[i] != "")
                    {
                        dataCount++;
                        Array.Resize(ref trimmedDataSplit, dataCount);
                        trimmedDataSplit[dataCount - 1] = dataSplit[i];
                    }
                }

                if (trimmedDataSplit.Length == 3)
                {
                    theseShapes[shapeCount].leftRough = Convert.ToSingle(trimmedDataSplit[0]);
                    theseShapes[shapeCount].rightRough = Convert.ToSingle(trimmedDataSplit[1]);
                    theseShapes[shapeCount].numPoints = Convert.ToInt16(trimmedDataSplit[2]);
                }
                else
                {
                    MessageBox.Show("Error importing .MAP file.");
                    e.Result = thisInst;
                    return;
                }

                theseShapes[shapeCount].isClosed = true;

                int pointCount = 0;
                theseShapes[shapeCount].points = new TopoInfo.UTM_X_Y[theseShapes[shapeCount].numPoints];

                while (pointCount < theseShapes[shapeCount].numPoints)
                {
                    thisDataString = sr.ReadLine();
                    dataSplit = thisDataString.Split(' ');

                    if (dataSplit != null)
                    {
                        dataCount = 0;
                        trimmedDataSplit = new string[0];
                        for (int i = 0; i < dataSplit.Length; i++)
                        {
                            if (dataSplit[i] != "")
                            {
                                dataCount++;
                                Array.Resize(ref trimmedDataSplit, dataCount);
                                trimmedDataSplit[dataCount - 1] = dataSplit[i];
                            }
                        }

                        for (int i = 0; i <= trimmedDataSplit.Length - 2; i = i + 2)
                        {
                            theseShapes[shapeCount].points[pointCount].UTMX = Convert.ToSingle(trimmedDataSplit[i]);
                            theseShapes[shapeCount].points[pointCount].UTMY = Convert.ToSingle(trimmedDataSplit[i + 1]);

                            if (theseShapes[shapeCount].points[pointCount].UTMX < topo.LC_NumXY.X.all.min) topo.LC_NumXY.X.all.min = theseShapes[shapeCount].points[pointCount].UTMX;
                            if (theseShapes[shapeCount].points[pointCount].UTMX > topo.LC_NumXY.X.all.max) topo.LC_NumXY.X.all.max = theseShapes[shapeCount].points[pointCount].UTMX;
                            if (theseShapes[shapeCount].points[pointCount].UTMY < topo.LC_NumXY.Y.all.min) topo.LC_NumXY.Y.all.min = theseShapes[shapeCount].points[pointCount].UTMY;
                            if (theseShapes[shapeCount].points[pointCount].UTMY > topo.LC_NumXY.Y.all.max) topo.LC_NumXY.Y.all.max = theseShapes[shapeCount].points[pointCount].UTMY;

                            pointCount++;
                        }
                    }
                }

                shapeCount++;
                textForProgBar = "Reading surface roughness file.";
                BackgroundWorker_WAsP_Map.ReportProgress(20, textForProgBar);
            }

            sr.Close();

            // Find Min/Max X/Y of each shape and determine if the shape is closed or if it is a line

            for (int i = 0; i < shapeCount; i++)
                topo.FindShapeMinMaxAndIsClosed(ref theseShapes[i]);

            // Now create land cover key: count all unique roughness values and assign default DH height to each

            topo.CreateLC_KeyUsingMAP_Shapes(theseShapes);

            topo.LC_NumXY.X.all.reso = 30;
            topo.LC_NumXY.Y.all.reso = 30;

            topo.LC_NumXY.X.all.num = Convert.ToInt16((topo.LC_NumXY.X.all.max - topo.LC_NumXY.X.all.min) / topo.LC_NumXY.X.all.reso + 1);
            topo.LC_NumXY.Y.all.num = Convert.ToInt16((topo.LC_NumXY.Y.all.max - topo.LC_NumXY.Y.all.min) / topo.LC_NumXY.Y.all.reso + 1);

            topo.landCover = new int[topo.LC_NumXY.X.all.num, topo.LC_NumXY.Y.all.num];

            // Traces each line of each shape and assigns LC codes to +/-90m from each line segment
            topo.AssignLC_CodesToShapeContours(theseShapes);

            // Now fill in blanks
            topo.FillInLC_Array();

            LandCover_table LC_Entry = new LandCover_table();

            int[] LCs_Along_X = new int[topo.LC_NumXY.Y.all.num];
            int entryInd = 0;
            int thisCount = 0;
            int numAll = topo.LC_NumXY.X.all.num * topo.LC_NumXY.Y.all.num;
            BinaryFormatter bin = new BinaryFormatter();
            string connString = nodeList.GetDB_ConnectionString(savedFilename);

            using (var ctx = new Continuum_EDMContainer(connString))
            {
                for (int i = 0; i <= topo.landCover.GetUpperBound(0); i++)
                {
                    for (int j = 0; j <= topo.landCover.GetUpperBound(1); j++)
                    {
                        thisCount++;

                        LCs_Along_X[entryInd] = topo.landCover[i, j];
                        entryInd++;

                        if (entryInd == topo.LC_NumXY.Y.all.num)
                        {
                            textForProgBar = "Reading in land cover...";
                            int Prog = Convert.ToInt16(100 * ((double)thisCount / (numAll - 1)));
                            BackgroundWorker_WAsP_Map.ReportProgress(Prog, textForProgBar);

                            MemoryStream MS2 = new MemoryStream();
                            bin.Serialize(MS2, LCs_Along_X);
                            LC_Entry.LandCover = MS2.ToArray();

                            ctx.LandCover_table.Add(LC_Entry);
                            ctx.SaveChanges();
                            entryInd = 0;
                            LCs_Along_X = new int[topo.LC_NumXY.Y.all.num];
                        }

                        if (BackgroundWorker_WAsP_Map.CancellationPending == true)
                        {
                            e.Cancel = true;
                            e.Result = thisInst;
                            return;
                        }
                    }
                }

                ctx.SaveChanges();
                ctx.Database.Connection.Close();
            }

            topo.useSR = true;
            topo.gotSR = true;
            topo.DecimateForPlot("LC");

            int numMets = metList.ThisCount;
            int numRad = radiiList.ThisCount;

            double[] overallWindRose = metList.GetAvgWindRose(thisInst.modeledHeight, Met.TOD.All, Met.Season.All);

            if (overallWindRose != null)
            {
                if (metList.ThisCount > 0)
                {
                    topo.GetElevsAndSRDH_ForCalcs(thisInst, null, false);

                    if (BackgroundWorker_WAsP_Map.CancellationPending == true)
                    {
                        e.Result = thisInst;
                        return;
                    }

                    for (int m = 0; m <= numMets - 1; m++)
                    {
                        for (int r = 0; r <= numRad - 1; r++)
                        {
                            if (BackgroundWorker_WAsP_Map.CancellationPending == true)
                            {
                                e.Cancel = true;
                                e.Result = thisInst;
                                return;
                            }
                            metList.CalcMetExposures(m, r, thisInst);
                        }
                    }

                    metList.SRDH_IsCalc = true;
                }

                if (turbList.TurbineCount > 0)
                {
                    numRad = radiiList.ThisCount;

                    for (int i = 0; i <= numRad - 1; i++)
                    {
                        int radius = radiiList.investItem[i].radius;
                        double exponent = radiiList.investItem[i].exponent;

                        if (BackgroundWorker_WAsP_Map.CancellationPending == true)
                        {
                            e.Cancel = true;
                            e.Result = thisInst;
                            return;
                        }

                        turbList.CalcTurbineExposures(thisInst, radius, exponent, 1);
                        turbList.turbineCalcsDone = false;

                        textForProgBar = "Calculating surface roughness at turbine sites.";
                        BackgroundWorker_WAsP_Map.ReportProgress((i + 1) / numRad * 100, textForProgBar);
                    }
                }
            }
            e.Result = thisInst;
        }

        private void BackgroundWorker_WAsP_Map_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates progress bar of .MAP import
            string Text_for_label = e.UserState.ToString();

            if (e.ProgressPercentage <= 100)
                progbar.Value = e.ProgressPercentage;
            else
                progbar.Value = 100;

            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            Refresh();
        }

        private void BackgroundWorker_WAsP_Map_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates topo map, turbine and met SRDH plots and tables after .MAP roughness file import

            if (e.Cancelled == false)
            {
                Continuum thisInst = (Continuum)e.Result;
                if (thisInst.IsDisposed == false)
                {
                    updateThe.AllTABs(thisInst);
                    thisInst.ChangesMade();
                }
            }

            Close();

        }

        /// <summary> Calls Turbine Calcs and Estimates background worker and runs background task. Calculates exposure and SRDH at each turbine site, calls DoTurbineCalcs, 
        /// generates avg WS estimates and energy estimate and wake loss calcs (if net calcs) </summary>        
        public void Call_BW_TurbCalcs(Vars_for_TurbCalcs Args)
        {            
            Show();
            BackgroundWorker_TurbCalcs.RunWorkerAsync(Args);
        }

        private void BackgroundWorker_TurbCalcs_DoWork(object sender, DoWorkEventArgs e)
        {
            Vars_for_TurbCalcs theArgs = (Vars_for_TurbCalcs)(e.Argument);
            Continuum thisInst = theArgs.thisInst;
            string MCP_Method = theArgs.MCP_Method; 
            Wake_Model wakeModel = theArgs.thisWakeModel;
            NodeCollection nodeList = new NodeCollection();
            TurbineCollection turbList = thisInst.turbineList;
            int numTurbs = turbList.TurbineCount;
         //   int numWD = thisInst.metList.numWD;
            
            DoWorkDone = false;
            WasReturned = false;

            string textForProgBar = "Turbine calculations starting...";
            BackgroundWorker_TurbCalcs.ReportProgress(0, textForProgBar);

            if (wakeModel != null)            
                textForProgBar = "Calculating wake losses at turbine sites.";            
            else            
                textForProgBar = "Calculating exposures at turbine sites.";            

            BackgroundWorker_TurbCalcs.ReportProgress(0, textForProgBar);
            thisInst.topo.GetElevsAndSRDH_ForCalcs(thisInst, null, false); // Get elevs for calcs           

            for (int i = 0; i < thisInst.radiiList.ThisCount; i++)
            {
                int radius = thisInst.radiiList.investItem[i].radius;
                double exponent = thisInst.radiiList.investItem[i].exponent;

                turbList.CalcTurbineExposures(thisInst, radius, exponent, 1);

                if (wakeModel != null)
                {
                    textForProgBar = "Calculating wake losses at turbine sites.";
                    BackgroundWorker_TurbCalcs.ReportProgress(0, textForProgBar);
                }
                else
                {
                    textForProgBar = "Calculating exposures at turbine sites.";
                    int prog = (int)((double)(i + 1) / thisInst.radiiList.ThisCount * 100);
                    BackgroundWorker_TurbCalcs.ReportProgress(prog, textForProgBar);
                }

                if (BackgroundWorker_TurbCalcs.CancellationPending == true)
                {
                    thisInst.wakeModelList.RemoveWakeModel(thisInst.turbineList, thisInst.mapList, wakeModel);
                    e.Result = thisInst;
                    WasReturned = true;
                    return;
                }
            }

            if (wakeModel != null)
            {
                textForProgBar = "Calculating wake losses at turbine sites.";
                BackgroundWorker_TurbCalcs.ReportProgress(0, textForProgBar);
            }
            else
            {
                if (turbList.TurbineCount > 0)
                {
                    textForProgBar = "Calculating wind speeds at 1/" + turbList.TurbineCount + " turbine sites.";
                    BackgroundWorker_TurbCalcs.ReportProgress(10, textForProgBar);
                }
            }

            // Calculate grid stats and flow separation nodes (if use flow separation model)
            for (int i = 0; i < turbList.TurbineCount; i++)
            {
                Turbine turbine = turbList.turbineEsts[i];

                if (thisInst.topo.useSepMod == true) turbine.GetFlowSepNodes(thisInst);

                if (turbine.gridStats.stats == null)
                    turbine.gridStats.GetGridArrayAndCalcStats(turbine.UTMX, turbine.UTMY, thisInst);
            }

            if ((thisInst.metList.isTimeSeries == false || thisInst.metList.isMCPd == false || thisInst.turbineList.genTimeSeries == false) && thisInst.modelList.ModelCount > 0)
            {
                for (int i = 0; i < turbList.TurbineCount; i++)
                {
                    Turbine turbine = turbList.turbineEsts[i];

                    if (turbine.AvgWSEst_Count == 0)
                    {
                        double[] windRose = thisInst.metList.GetInterpolatedWindRose(thisInst.metList.GetMetsUsed(), turbine.UTMX, turbine.UTMY, Met.TOD.All, Met.Season.All, thisInst.modeledHeight);
                        Model[] models = thisInst.modelList.GetAllModels(thisInst, thisInst.metList.GetMetsUsed());
                        turbine.DoTurbineCalcs(thisInst, models);
                        turbine.GenerateAvgWSFromTABs(thisInst, models, windRose, false); // calculates avg WS est at turb and uncertainty of WS   

                        int prog = (int)(100.0f * (i + 1) / numTurbs);
                        textForProgBar = "Calculating wind speeds at " + (i + 1) + "/" + turbList.TurbineCount + " turbine sites.";
                        BackgroundWorker_TurbCalcs.ReportProgress(prog, textForProgBar);
                    }

                    if (BackgroundWorker_TurbCalcs.CancellationPending == true)
                    {
                        thisInst.wakeModelList.RemoveWakeModel(thisInst.turbineList, thisInst.mapList, wakeModel);
                        e.Result = thisInst;
                        WasReturned = true;
                        return;
                    }
                }
            }
            else
            {
                for (int i = 0; i < turbList.TurbineCount; i++)
                {
                    Turbine turbine = turbList.turbineEsts[i];

                    // Generate turbine wind speed estimates using seasonal/diurnal time series models. Use blank power curves and wake models, only getting free-stream WS ests here 

                    Nodes targetNode = nodeList.GetTurbNode(turbine);
                    if (thisInst.turbineList.PowerCurveCount == 0) // just do Avg_WS ests
                    {
                        // Check to see if gross ests have already been done
                        bool haveWS = turbine.HaveTS_Estimate("WS", null, new TurbineCollection.PowerCurve());

                        if (haveWS == false)
                        {
                            ModelCollection.TimeSeries[] thisTS = thisInst.modelList.GenerateTimeSeries(thisInst, thisInst.metList.GetMetsUsed(), targetNode, new TurbineCollection.PowerCurve(),
                                null, null, MCP_Method);

                            turbine.GenerateAvgWSTimeSeries(thisTS, thisInst, new Wake_Model(), false, MCP_Method, false, new TurbineCollection.PowerCurve());  // Creates and adds new Avg_Est based on time series data
                        }
                    }
                    else
                    {
                        for (int p = 0; p < turbList.PowerCurveCount; p++)
                        {
                            bool haveGross = turbine.HaveTS_Estimate("Gross", null, turbList.powerCurves[p]);
                            bool wakeModelUsesCrv = thisInst.wakeModelList.HaveWakeModelWithThisCrv(turbList.powerCurves[p]);

                            if (haveGross == false && wakeModelUsesCrv == false)
                            {
                                ModelCollection.TimeSeries[] thisTS = thisInst.modelList.GenerateTimeSeries(thisInst, thisInst.metList.GetMetsUsed(), targetNode, turbList.powerCurves[p],
                                    null, null, MCP_Method);

                                turbine.GenerateAvgWSTimeSeries(thisTS, thisInst, new Wake_Model(), false, MCP_Method, false, turbList.powerCurves[p]);  // Creates and adds new Avg_Est based on time series data
                                turbine.CalcGrossAEPFromTimeSeries(thisInst, thisTS, turbList.powerCurves[p]); // Calculates and adds gross energy estimate based on energy production time series                                                                
                            }
                        }

                    }

                    //  counter++;

                    int prog = (int)(100.0f * (i + 1) / numTurbs);
                    textForProgBar = "Calculating wind speeds at " + (i + 1) + "/" + turbList.TurbineCount + " turbine sites.";
                    BackgroundWorker_TurbCalcs.ReportProgress(prog, textForProgBar);

                    if (BackgroundWorker_TurbCalcs.CancellationPending == true)
                    {
                        thisInst.wakeModelList.RemoveWakeModel(thisInst.turbineList, thisInst.mapList, wakeModel);
                        e.Result = thisInst;
                        WasReturned = true;
                        return;
                    }
                }
            }                        

            if ((thisInst.metList.isTimeSeries == false || thisInst.metList.isMCPd == false || turbList.genTimeSeries == false) && thisInst.modelList.ModelCount > 0 && thisInst.turbineList.PowerCurveCount > 0) // Gross estimates using time series calculated earlier
            {
                textForProgBar = "Calculating gross AEP at turbine sites.";
                BackgroundWorker_TurbCalcs.ReportProgress(90, textForProgBar);                                
                turbList.CalcGrossAEPFromTABs(thisInst);
            }

            BackgroundWorker_TurbCalcs.ReportProgress(0, textForProgBar);

            // Go through list of wake models and do calculations for those that don't exist yet
            for (int w = 0; w < thisInst.wakeModelList.NumWakeModels; w++)
            {
                Wake_Model thisWakeModel = thisInst.wakeModelList.wakeModels[w];
                if (turbList.turbineEsts[0].EstsExistForWakeModel(thisWakeModel, thisInst.wakeModelList) == false)
                {
                    // Find wake loss coeffs                    
                    int minDistance = 10000000;
                    int maxDistance = 0;

                    for (int i = 0; i < numTurbs; i++)
                    {
                        int[] Min_Max_Dist = turbList.CalcMinMaxDistanceToTurbines(turbList.turbineEsts[i].UTMX, turbList.turbineEsts[i].UTMY);
                        if (Min_Max_Dist[0] < minDistance) minDistance = Min_Max_Dist[0]; // this is min distance to turbine but when WD is at a different angle (not in line with turbines) the X dist is less than this value so making this always equal to 2*RD
                        if (Min_Max_Dist[1] > maxDistance) maxDistance = Min_Max_Dist[1];
                    }

                    minDistance = (int)(2 * thisWakeModel.powerCurve.RD);
                    if (maxDistance == 0) maxDistance = 15000; // maxDistance will be zero when there is only one turbine. Might be good to make this value constant
                    WakeCollection.WakeLossCoeffs[] wakeCoeffs = thisInst.wakeModelList.GetWakeLossesCoeffs(minDistance, maxDistance, thisWakeModel, thisInst.metList);
                    
                    //   counter = 0;
                    //   integerList = Enumerable.Range(0, thisInst.turbineList.TurbineCount).ToList();
                    //   Parallel.ForEach(integerList, i =>
                    //   {

                    for (int i = 0; i < numTurbs; i++)
                    {
                        textForProgBar = "Calculating wake losses and net estimates at turbine sites. " + ((i + 1) / (float)numTurbs).ToString("P") + " complete";
                        BackgroundWorker_TurbCalcs.ReportProgress((int)(100 * (i + 1) / (float)numTurbs), textForProgBar);

                        if (BackgroundWorker_TurbCalcs.CancellationPending == true)
                        {
                            thisInst.wakeModelList.RemoveWakeModel(thisInst.turbineList, thisInst.mapList, wakeModel);
                            turbList.CleanUpEsts();
                            thisInst.wakeModelList.CleanUpWakeModelsAndGrid();
                            e.Result = thisInst;
                            WasReturned = true;
                            return;
                        }

                        if ((thisInst.metList.isTimeSeries == false || thisInst.metList.isMCPd == false || turbList.genTimeSeries == false) && thisInst.modelList.ModelCount > 0)
                        {
                            turbList.turbineEsts[i].CalcTurbineWakeLosses(thisInst, wakeCoeffs, thisWakeModel);
                        }
                        else if (thisInst.modelList.ModelCount > 0)
                        {
                            // Default model net wind speed and energy production
                            Nodes targetNode = nodeList.GetTurbNode(turbList.turbineEsts[i]);

                            bool haveNetTS = turbList.turbineEsts[i].HaveTS_Estimate("Net", thisWakeModel, thisWakeModel.powerCurve);

                            if (haveNetTS == false)
                            {
                                ModelCollection.TimeSeries[] thisTS = thisInst.modelList.GenerateTimeSeries(thisInst, thisInst.metList.GetMetsUsed(), targetNode, thisWakeModel.powerCurve, thisWakeModel,
                                    wakeCoeffs, MCP_Method);

                                turbList.turbineEsts[i].GenerateAvgWSTimeSeries(thisTS, thisInst, thisWakeModel, false, MCP_Method, false, thisWakeModel.powerCurve);
                                turbList.turbineEsts[i].CalcGrossAEPFromTimeSeries(thisInst, thisTS, thisWakeModel.powerCurve);
                                turbList.turbineEsts[i].CalcNetAEPFromTimeSeries(thisInst, thisTS, thisWakeModel.powerCurve, thisWakeModel);
                            }
                        }

                        //   counter++;

                    }
                    //  });
                }
            }

            turbList.AssignStringNumber();
            turbList.AreTurbCalcsDone(thisInst);
            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_TurbCalcs_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the Turbine Calcs progress bar
            string Text_for_label = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            BringToFront();
            Refresh();
        }

        private void BackgroundWorker_TurbCalcs_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates the turbine estimates on main form

            Continuum thisInst = (Continuum)e.Result;

            if (thisInst.IsDisposed == false)
            {
                updateThe.AllTABs(thisInst);
                thisInst.ChangesMade();
            }            

            Close();
        }

        /// <summary> Calls background worker to generate map </summary>        
        public void Call_BW_GenMap(Vars_for_Gen_Map Args)
        {            
            Show();
            BackgroundWorker_Map.RunWorkerAsync(Args);
        }
        
        private void BackgroundWorker_Map_DoWork(object sender, DoWorkEventArgs e)
        {
            // for each map node, calculates exposure, SRDH, grid stats, and calls DoMapCalcs
            // Generates a map using the Background worker
            DoWorkDone = false;
            Vars_for_Gen_Map theArgs = (Vars_for_Gen_Map)(e.Argument);

            Continuum thisInst = theArgs.thisInst;
            TopoInfo topo = thisInst.topo;
            Map thisMap = theArgs.thisMap;
            string MCP_Method = theArgs.MCP_Method;      
            MetCollection metList = thisInst.metList;
            NodeCollection nodeList = new NodeCollection();        
            TurbineCollection turbList = thisInst.turbineList;        
            WakeCollection wakeModelList = thisInst.wakeModelList;
                  
            int numWD = thisInst.metList.numWD;

            int numX = thisMap.numX;
            int numY = thisMap.numY;
            int numMapNodes = numX * numY;

            int wakeGridInd = wakeModelList.GetWakeGridInd(thisMap.wakeModel, thisMap.minUTMX, thisMap.minUTMY, thisMap.numX, thisMap.numY, thisMap.reso);

            string textForProgBar = "Retrieving elevation data from local database...";

            int mapNodeCount = 0;
            BackgroundWorker_Map.ReportProgress(0, textForProgBar);

            // Get elevs for calcs
            topo.GetElevsAndSRDH_ForCalcs(thisInst, thisMap, false);

            if (thisMap.parameterToMap == null)
            {
                thisMap.parameterToMap = new double[numX, numY];                
                thisMap.sectorParamToMap = new double[numX, numY, numWD];
            }

            Stopwatch thisStopwatch = new Stopwatch();
            thisStopwatch.Start();

            double timeElapsed = 0;            
            double timeToFinish;
            
     //       NodeCollection.Path_of_Nodes_w_Rad_and_Met_Name[] pathsToMets = null;

      //      Nodes[] allNodesInX = new Nodes[numY];
      //      Nodes firstNodeLastCol = new Nodes();

            Nodes[] nodesToAdd = new Nodes[1];
       //     Nodes[] nodesFromDB = null;

            WakeCollection.WakeLossCoeffs[] wakeCoeffs = null;
            
            if (thisMap.isWaked == true)
            {
                // Find wake loss coeffs

                int minDist = (int)(2 * thisMap.wakeModel.powerCurve.RD);
                int maxDist = 0;

                int[] minMaxDist = turbList.CalcMinMaxDistanceToTurbines(thisMap.minUTMX, thisMap.minUTMY);
                if (minMaxDist[1] > maxDist) maxDist = minMaxDist[1];

                minMaxDist = turbList.CalcMinMaxDistanceToTurbines(thisMap.minUTMX, thisMap.minUTMY + thisMap.numY * thisMap.reso);
                if (minMaxDist[1] > maxDist) maxDist = minMaxDist[1];

                minMaxDist = turbList.CalcMinMaxDistanceToTurbines(thisMap.minUTMX + thisMap.numX * thisMap.reso, thisMap.minUTMY);
                if (minMaxDist[1] > maxDist) maxDist = minMaxDist[1];

                minMaxDist = turbList.CalcMinMaxDistanceToTurbines(thisMap.minUTMX + thisMap.numX * thisMap.reso, thisMap.minUTMY + thisMap.numY * thisMap.reso);
                if (minMaxDist[1] > maxDist) maxDist = minMaxDist[1];

                wakeCoeffs = wakeModelList.GetWakeLossesCoeffs(minDist, maxDist, thisMap.wakeModel, metList);
            }

            for (int xind = 0; xind < numX; xind++)
            {
                double thisX = thisMap.minUTMX + xind * thisMap.reso;
             //   int minY = thisMap.minUTMY;
             //   int maxY = thisMap.minUTMY + thisMap.numY * thisMap.reso;

              //  nodesFromDB = nodeList.GetNodes(thisX, minY, thisX, maxY, thisInst, false); // To do: this isn't used anywhere...

                for (int yind = 0; yind <= numY - 1; yind++)
                {
                    mapNodeCount++;

                    if (thisMap.parameterToMap[xind, yind] == 0)
                    {
                        double thisY = thisMap.minUTMY + yind * thisMap.reso;
                        Map.MapNode thisMapNode = new Map.MapNode();
                        thisMapNode.UTMX = thisX;
                        thisMapNode.UTMY = thisY;
                        Nodes thisNode = nodeList.GetANode(thisX, thisY, thisInst);
                        thisMapNode.elev = thisNode.elev;
                        thisMapNode.expo = thisNode.expo;
                        thisMapNode.gridStats = thisNode.gridStats;
                     
                        if (BackgroundWorker_Map.CancellationPending == true)
                        {
                            //  nodeList.AddNodes(Nodes_to_add, thisInst.savedParams.savedFileName);                            
                            e.Result = thisInst;
                            return;
                        }

                        if ((thisMapNode.expo == null) || (thisMapNode.gridStats.stats == null))
                        {
                            if (thisMapNode.expo == null)
                                thisMap.CalcMapExposures(ref thisMapNode, 1, thisInst);

                            if (thisMapNode.gridStats.stats == null)
                                thisMapNode.gridStats.GetGridArrayAndCalcStats(thisMapNode.UTMX, thisMapNode.UTMY, thisInst);

                            nodesToAdd[0] = nodeList.GetMapAsNode(thisMapNode);
                            nodeList.AddNodes(nodesToAdd, thisInst.savedParams.savedFileName);
                        }

                        if (thisInst.metList.isTimeSeries == false || thisInst.metList.isMCPd == false || thisMap.useTimeSeries == false || thisMap.modelType <= 1)
                        {
                           // if (mapNodeCount > 0 && mapNodeCount % 10 == 0)
                          //  {
                                timeElapsed = (thisStopwatch.Elapsed.TotalSeconds - timeElapsed);
                                double avgTimePerNode = (thisStopwatch.Elapsed.TotalSeconds / (mapNodeCount + 1));
                                timeToFinish = (numMapNodes - mapNodeCount) * avgTimePerNode / 60;
                                textForProgBar = "Node " + mapNodeCount + "/" + numMapNodes + " Avg time/node: " + Math.Round(avgTimePerNode, 1) +
                                    " secs." + " Est. time to finish: " + Math.Round(timeToFinish, 1) + " mins.";
                                int Prog = Convert.ToInt16(100.0f * mapNodeCount / numMapNodes);
                                BackgroundWorker_Map.ReportProgress(Prog, textForProgBar);
                          //  }
                            
                            if (thisMapNode.windRose == null)
                                thisMapNode.windRose = metList.GetInterpolatedWindRose(metList.GetMetsUsed(), thisMapNode.UTMX, thisMapNode.UTMY, Met.TOD.All, Met.Season.All, thisInst.modeledHeight);

                            if (thisMap.useFlowSep == true) thisMap.GetFlowSepNodes(ref thisMapNode, thisInst);

                            if (thisMap.modelType == 2 || thisMap.modelType == 3)
                                thisMap.DoMapCalcs(ref thisMapNode, thisInst);
                            
                            // Combine WS ests from various mets into one average
                            if (thisMap.modelType >= 2)
                                thisMap.GenerateAvgWS_AtOneMapNode(ref thisMapNode, thisInst);

                            if ((thisMap.modelType == 5 || thisMap.modelType == 3) && thisMapNode.avgWS_Est != 0)
                            {
                                thisMap.CalcWS_DistAtMapNode(ref thisMapNode, metList, numWD, thisInst.modeledHeight);
                                thisMap.CalcGrossAEP_AtMapNode(ref thisMapNode, metList, turbList);
                            }

                            if (thisMap.isWaked)
                                thisMap.CalcWakeLossesMap(ref thisMapNode, thisInst, thisMap.wakeModel, wakeCoeffs);

                        }
                        else // Time Series model
                        {
                            timeElapsed = (thisStopwatch.Elapsed.TotalSeconds - timeElapsed);
                            double avgTimePerNode = (thisStopwatch.Elapsed.TotalSeconds / (mapNodeCount + 1));
                            timeToFinish = (numMapNodes - mapNodeCount) * avgTimePerNode / 60;
                            textForProgBar = "Node " + mapNodeCount + "/" + numMapNodes + " Avg time/node: " + Math.Round(avgTimePerNode, 1) +
                                " secs." + " Est. time to finish: " + Math.Round(timeToFinish, 1) + " mins.";
                            int Prog = Convert.ToInt16(100.0f * mapNodeCount / numMapNodes);
                            BackgroundWorker_Map.ReportProgress(Prog, textForProgBar);

                            TurbineCollection.PowerCurve thisPowerCurve = thisInst.turbineList.GetPowerCurve(thisMap.powerCurve);
                            Nodes targetNode = nodeList.GetMapAsNode(thisMapNode);
                            ModelCollection.TimeSeries[] thisTS = thisInst.modelList.GenerateTimeSeries(thisInst, thisInst.metList.GetMetsUsed(), targetNode, 
                                thisPowerCurve, thisMap.wakeModel, wakeCoeffs, MCP_Method);

                            string wakedOrFreestream = "Freestream";
                            if (thisMap.isWaked)
                                wakedOrFreestream = "Waked";

                            Met.WSWD_Dist thisDist = thisInst.modelList.CalcWSWD_Dist(thisTS, thisInst, wakedOrFreestream);
                            thisMapNode.avgWS_Est = thisDist.WS;
                            thisMapNode.sectorWS = thisDist.sectorWS_Ratio;
                            thisMapNode.sectDist = thisDist.sectorWS_Dist;
                            thisMapNode.windRose = thisDist.windRose;
                            thisMapNode.WS_Dist = thisDist.WS_Dist;

                            for (int i = 0; i < thisDist.sectorWS_Ratio.Length; i++)
                                thisMapNode.sectorWS[i] = thisMapNode.sectorWS[i] * thisMapNode.avgWS_Est;

                            if (thisMap.modelType == 3 || thisMap.modelType == 5)
                            {
                                if (thisMap.isWaked == false) // gross AEP
                                {
                                    Turbine.Gross_Energy_Est thisGross = new Turbine.Gross_Energy_Est();
                                    thisInst.modelList.CalcGrossAEP_AndMonthlyEnergy(ref thisGross, thisTS, thisInst);
                                    thisMapNode.grossAEP = thisGross.AEP;
                                    thisMapNode.sectorGross = thisGross.sectorEnergy;
                                }
                                else
                                {
                                    Turbine.Net_Energy_Est thisNet = new Turbine.Net_Energy_Est();
                                    thisNet.wakeModel = thisMap.wakeModel;                              
                                    thisInst.modelList.CalcNetAEP_AndMonthlyEnergy(ref thisNet, thisTS, thisInst);
                                    thisMapNode.netEnergyEsts.est = thisNet.AEP;
                                    thisMapNode.netEnergyEsts.sectorEnergy = thisNet.sectorEnergy;
                                    thisMapNode.netEnergyEsts.wakeLoss = thisNet.wakeLoss;
                                    thisMapNode.netEnergyEsts.sectorWakeLoss = thisNet.sectorWakeLoss;

                                    // Get gross estimates to calculate wake loss
                                    ModelCollection.TimeSeries[] grossTS = thisInst.modelList.GenerateTimeSeries(thisInst, thisInst.metList.GetMetsUsed(), targetNode,
                                        thisPowerCurve, null, null, MCP_Method);                                    
                                    Turbine.Gross_Energy_Est thisGross = new Turbine.Gross_Energy_Est();
                                    thisInst.modelList.CalcGrossAEP_AndMonthlyEnergy(ref thisGross, grossTS, thisInst);
                                    thisMapNode.grossAEP = thisGross.AEP;
                                    thisMapNode.sectorGross = thisGross.sectorEnergy;
                                    thisMapNode.sectDist = thisDist.sectorWS_Dist;
                                    double otherLoss = thisInst.turbineList.exceed.GetOverallPValue_1yr(50);
                                    thisMapNode.netEnergyEsts.wakeLoss = thisInst.wakeModelList.CalcThisWakeLoss(thisNet.AEP, thisGross.AEP, otherLoss);
                                    thisMapNode.netEnergyEsts.sectorWakeLoss = thisInst.wakeModelList.CalcThisSectWakeLoss(thisNet.sectorEnergy, thisGross.sectorEnergy, otherLoss);
                                }
                            }
                        }

                        if (thisMap.isWaked)
                            wakeModelList.PopulateWakeGrid(thisMapNode, wakeGridInd);

                        // Finally, populate parameterToMap 

                        if (thisMap.modelType == 0)  // UW exposure
                        {
                            thisMap.parameterToMap[xind, yind] = thisMapNode.expo[thisMap.expoMapRadius].GetOverallValue(thisMapNode.windRose, "Expo", "UW");
                            for (int WD = 0; WD < numWD; WD++)
                                thisMap.sectorParamToMap[xind, yind, WD] = thisMapNode.expo[thisMap.expoMapRadius].expo[WD];
                        }                            
                        else if (thisMap.modelType == 1)  // DW Exposure
                        {
                            thisMap.parameterToMap[xind, yind] = thisMapNode.expo[thisMap.expoMapRadius].GetOverallValue(thisMapNode.windRose, "Expo", "DW");
                            for (int WD = 0; WD < numWD; WD++)
                                thisMap.sectorParamToMap[xind, yind, WD] = thisMapNode.expo[thisMap.expoMapRadius].GetDW_Param(WD, "Expo");
                        }                            
                        else if (thisMap.modelType == 2 || thisMap.modelType == 4)
                        {
                            thisMap.parameterToMap[xind, yind] = thisMapNode.avgWS_Est;
                            for (int WD = 0; WD < numWD; WD++)
                                thisMap.sectorParamToMap[xind, yind, WD] = thisMapNode.sectorWS[WD];
                        }
                        else if (thisMap.isWaked == false && (thisMap.modelType == 3 || thisMap.modelType == 5))  // AEP Best UWSW
                            thisMap.parameterToMap[xind, yind] = thisMapNode.grossAEP;
                        else if (thisMap.isWaked)
                        {
                            thisMap.parameterToMap[xind, yind] = thisMapNode.avgWS_Est;
                            for (int WD = 0; WD < numWD; WD++)
                                thisMap.sectorParamToMap[xind, yind, WD] = thisMapNode.sectorWS[WD];
                        }
                    }
                }
            }

            thisMap.isComplete = true;
            if (thisMap.isWaked)
                wakeModelList.wakeGridMaps[wakeGridInd].isComplete = true;

            DoWorkDone = true;
            e.Result = thisInst;

        }

        private void BackgroundWorker_Map_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the map generation progress bar
            BringToFront();
            string Text_for_label = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            Refresh();
        }

        private void BackgroundWorker_Map_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates map list and wake map list on main form
            Continuum thisInst = (Continuum)e.Result;

            if (thisInst.IsDisposed == false)
            {
                updateThe.MapsTAB(thisInst);
                updateThe.NetTurbineEstsTAB(thisInst);

                thisInst.SaveFile(false);
            }

            Close();
        }

        /// <summary> Calls Round Robin ucertainty analysis background worker </summary>        
        public void Call_BW_RoundRobin(Vars_for_RoundRobin Args)
        {            
            Show();
            BackgroundWorker_RoundRobin.RunWorkerAsync(Args);
        }

        private void BackgroundWorker_RoundRobin_DoWork(object sender, DoWorkEventArgs e)
        {           
            Vars_for_RoundRobin theArgs = (Vars_for_RoundRobin)e.Argument;

            Continuum thisInst = theArgs.thisInst;
            string MCP_Method = theArgs.MCP_Method;
            MetCollection metList = thisInst.metList;
            MetPairCollection metPairList = thisInst.metPairList;

            int minRR_Size = theArgs.Min_RR_Size;
            string[] metsUsed = metList.GetMetsUsed();

            int numMets = metsUsed.Length;                        
            MetPairCollection.RR_funct_obj[] RR_obj_coll = new MetPairCollection.RR_funct_obj[1];

            string textForProgBar = "Preparing for Round Robin Analysis";
            BackgroundWorker_RoundRobin.ReportProgress(0, textForProgBar);

            for (int n = metsUsed.Length - minRR_Size; n <= numMets - minRR_Size; n++)
            {
                int numMetsInModel = metsUsed.Length - n;                
                bool RR_Done = metPairList.RR_DoneAlready(metsUsed, numMetsInModel, metList);

                if (RR_Done == false)
                {
                    int numModels = metPairList.GetNumModelsForRoundRobin(numMets, numMetsInModel);
                    string[,] metsForModels = metPairList.GetAllCombos(metsUsed, numMets, numMetsInModel, numModels);

                    for (int i = 0; i <= numModels - 1; i++)
                    {
                        string[] metsForThisModel = new string[numMetsInModel];

                        for (int j = 0; j <= numMetsInModel - 1; j++)
                            metsForThisModel[j] = metsForModels[j, i];

                        textForProgBar = "Calculating model error using " + (metsUsed.Length - n).ToString() + " met sites : " + i + "/" + numModels;
                        BackgroundWorker_RoundRobin.ReportProgress(100 * i / numModels, textForProgBar);

                        MetPairCollection.RR_funct_obj thisRR_obj = metPairList.DoRR_Calc(metsForThisModel, thisInst, metsUsed, MCP_Method);
                        Array.Resize(ref RR_obj_coll, i + 1);
                        RR_obj_coll[i] = thisRR_obj;

                        if (BackgroundWorker_RoundRobin.CancellationPending == true)
                        {
                            e.Result = thisInst;
                            return;
                        }
                    }

                    metPairList.AddRoundRobinEst(RR_obj_coll, metsUsed, numMetsInModel, metsForModels);
                }
            }

            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_RoundRobin_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the Round Robin progress bar
            string Text_for_label = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            Refresh();
        }

        private void BackgroundWorker_RoundRobin_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates Round Robin dropdown menu on main form

            if (e.Cancelled == false)            
            {
                Continuum thisInst = (Continuum)e.Result;
                if (thisInst.IsDisposed == false)
                {
                    updateThe.Uncertainty_TAB_Round_Robin(thisInst);
                    thisInst.ChangesMade();
                }
            }

            Close();
        }

        /// <summary> Calls Save As background worker. Saves CFM file and a copy of database at new save location </summary>        
        public void Call_BW_SaveAs(Vars_for_Save_As Args)
        {            
            Show();
            BackgroundWorker_SaveAs.RunWorkerAsync(Args);
        }

        private void BackgroundWorker_SaveAs_DoWork(object sender, DoWorkEventArgs e)
        {            
            NodeCollection nodeList = new NodeCollection();
            DoWorkDone = false;
            Vars_for_Save_As The_args = (Vars_for_Save_As)e.Argument;
            string newFilename = The_args.newFilename;
            string oldFilename = The_args.oldFilename;

            string newConnString = nodeList.GetDB_ConnectionString(newFilename);
            string oldConnString = nodeList.GetDB_ConnectionString(oldFilename);

            // Check to see if there is a database to copy over
            if (File.Exists(oldFilename) == true)
            {
                string textForProgBar = "Preparing to copy database...";
                BackgroundWorker_SaveAs.ReportProgress(0, textForProgBar);
                // Copy Nodes (and expo, GridStats, MapNodes and MapWSEsts)
                int numNodes;

                // Delete old database if there is one
                try
                {
                    using (var ctx = new Continuum_EDMContainer(newConnString))
                    {
                        if (ctx.Database.Exists())
                            ctx.Database.Delete();

                        ctx.Database.Connection.Close();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.InnerException.ToString());
                    return;
                }

                // Copy over node data
                try
                {
                    using (var ctx = new Continuum_EDMContainer(oldConnString))
                    {
                        numNodes = ctx.Node_table.Count();
                        ctx.Database.Connection.Close();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.InnerException.ToString());
                    return;
                }

                int minId = 1;
                int maxId;
                Node_table[] nodeDB = new Node_table[2000];
                if (numNodes > 2000)
                    maxId = minId + 1999;
                else
                {
                    maxId = numNodes;
                    nodeDB = new Node_table[numNodes];
                }
                bool gotThemAll = false;

                while (gotThemAll == false && numNodes > 0)
                {
                    textForProgBar = "Saving node data...";
                    int prog = (int)(100 * (double)maxId / numNodes);
                    BackgroundWorker_SaveAs.ReportProgress(prog, textForProgBar);
                    int dataInd = 0;

                    // Copy 20000 entries from old DB
                    try
                    {
                        using (var ctx = new Continuum_EDMContainer(oldConnString))
                        {
                            for (int This_Id = minId; This_Id <= maxId; This_Id++)
                            {
                                var node_expo_db = from N in ctx.Node_table.Include("expo") where N.Id == This_Id select N;

                                foreach (var N in node_expo_db)
                                {
                                    nodeDB[dataInd] = new Node_table();
                                    nodeDB[dataInd].UTMX = N.UTMX;
                                    nodeDB[dataInd].UTMY = N.UTMY;
                                    nodeDB[dataInd].elev = N.elev;

                                    int numExpos = N.expo.Count();

                                    for (int expInd = 0; expInd < numExpos; expInd++)
                                    {
                                        Expo_table newExpo = new Expo_table();
                                        newExpo.Expo_Array = N.expo.ElementAt(expInd).Expo_Array;
                                        newExpo.ExpoDist_Array = N.expo.ElementAt(expInd).ExpoDist_Array;
                                        newExpo.radius = N.expo.ElementAt(expInd).radius;
                                        newExpo.exponent = N.expo.ElementAt(expInd).exponent;
                                        newExpo.UW_Cross_Grade = N.expo.ElementAt(expInd).UW_Cross_Grade;
                                        newExpo.UW_ParallelGrade = N.expo.ElementAt(expInd).UW_ParallelGrade;
                                        newExpo.SR_Array = N.expo.ElementAt(expInd).SR_Array;
                                        newExpo.DH_Array = N.expo.ElementAt(expInd).DH_Array;
                                        nodeDB[dataInd].expo.Add(newExpo);
                                    }
                                }

                                var node_gridstat_db = from N in ctx.Node_table.Include("GridStats") where N.Id == This_Id select N;

                                foreach (var N in node_gridstat_db)
                                {
                                    int numGridStats = N.GridStats.Count();
                                    for (int gridStatInd = 0; gridStatInd <= numGridStats - 1; gridStatInd++)
                                    {
                                        GridStat_table newGridStat = new GridStat_table();
                                        newGridStat.radius = N.GridStats.ElementAt(gridStatInd).radius;
                                        newGridStat.P10_UW = N.GridStats.ElementAt(gridStatInd).P10_UW;
                                        newGridStat.P10_DW = N.GridStats.ElementAt(gridStatInd).P10_DW;
                                        nodeDB[dataInd].GridStats.Add(newGridStat);
                                    }
                                }

                                dataInd++;
                            }
                            ctx.Database.Connection.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.InnerException.ToString());
                        return;
                    }

                    try
                    {
                        using (var ctx = new Continuum_EDMContainer(newConnString))
                        {
                            ctx.Node_table.AddRange(nodeDB);
                            ctx.SaveChanges();
                            dataInd = 0;
                            nodeDB = new Node_table[2000];
                            ctx.Database.Connection.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.InnerException.ToString());
                        return;
                    }

                    if (maxId == numNodes)
                        gotThemAll = true;

                    minId = maxId + 1;
                    maxId = maxId + 2000;

                    if (maxId > numNodes)
                    {
                        maxId = numNodes;
                        nodeDB = new Node_table[maxId - minId + 1];
                    }
                }

                // Copy over topo data
                int numTopo = 0;
                try
                {
                    using (var ctx = new Continuum_EDMContainer(oldConnString))
                    {
                        numTopo = ctx.Topo_table.Count();
                        ctx.Database.Connection.Close();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.InnerException.ToString());
                    return;
                }

                minId = 1;
                Topo_table[] topoDB = new Topo_table[20000];
                if (numTopo > 20000)
                    maxId = minId + 19999;
                else
                {
                    maxId = numTopo;
                    topoDB = new Topo_table[numTopo];
                }

                gotThemAll = false;

                while (gotThemAll == false && numTopo > 0)
                {
                    textForProgBar = "Saving topography data...";
                    int prog = (int)(100 * (double)maxId / numTopo);
                    BackgroundWorker_SaveAs.ReportProgress(prog, textForProgBar);
                    int dataInd = 0;
                    // Copy 20000 entries from old DB

                    try
                    {
                        using (var ctx = new Continuum_EDMContainer(oldConnString))
                        {
                            var topo_exist_db = from N in ctx.Topo_table where N.Id >= minId && N.Id <= maxId select N;

                            foreach (var N in topo_exist_db)
                            {
                                topoDB[dataInd] = new Topo_table();
                                topoDB[dataInd].Elevs = N.Elevs;
                                dataInd++;
                            }
                            ctx.Database.Connection.Close();
                        }

                        using (var ctx = new Continuum_EDMContainer(newConnString))
                        {
                            ctx.Topo_table.AddRange(topoDB);
                            ctx.SaveChanges();
                            dataInd = 0;
                            topoDB = new Topo_table[20000];
                            ctx.Database.Connection.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.InnerException.ToString());
                        return;
                    }

                    if (maxId == numTopo)
                        gotThemAll = true;

                    minId = maxId + 1;
                    maxId = maxId + 20000;

                    if (maxId > numTopo)
                    {
                        maxId = numTopo;
                        topoDB = new Topo_table[maxId - minId + 1];
                    }
                }

                // Copy over land cover data
                int numLC = 0;
                try
                {
                    using (var ctx = new Continuum_EDMContainer(oldConnString))
                    {
                        numLC = ctx.LandCover_table.Count();
                        ctx.Database.Connection.Close();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.InnerException.ToString());
                    return;
                }

                minId = 1;
                LandCover_table[] landCoverDB = new LandCover_table[20000];
                if (numLC > 20000)
                    maxId = minId + 19999;
                else
                {
                    maxId = numLC;
                    landCoverDB = new LandCover_table[numLC];
                }

                gotThemAll = false;

                while (numLC > 0 && gotThemAll == false)
                {
                    textForProgBar = "Saving land cover data...";
                    int prog = (int)(100 * (double)maxId / numLC);
                    BackgroundWorker_SaveAs.ReportProgress(prog, textForProgBar);
                    int dataInd = 0;
                    // Copy 20000 entries from old DB

                    try
                    {
                        using (var ctx = new Continuum_EDMContainer(oldConnString))
                        {
                            var lc_exist_db = from N in ctx.LandCover_table where N.Id >= minId && N.Id <= maxId select N;

                            foreach (var N in lc_exist_db)
                            {
                                landCoverDB[dataInd] = new LandCover_table();
                                landCoverDB[dataInd].LandCover = N.LandCover;
                                dataInd++;
                            }
                            ctx.Database.Connection.Close();
                        }

                        using (var ctx = new Continuum_EDMContainer(newConnString))
                        {
                            ctx.LandCover_table.AddRange(landCoverDB);
                            ctx.SaveChanges();
                            dataInd = 0;
                            landCoverDB = new LandCover_table[20000];
                            ctx.Database.Connection.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.InnerException.ToString());
                        return;
                    }

                    if (maxId == numLC)
                        gotThemAll = true;

                    minId = maxId + 1;
                    maxId = maxId + 20000;

                    if (maxId > numLC)
                    {
                        maxId = numLC;
                        landCoverDB = new LandCover_table[maxId - minId + 1];
                    }
                }

                // Copy over MERRA2 data
                int numMERRA2Nodes = 0;
                try
                {
                    using (var ctx = new Continuum_EDMContainer(oldConnString))
                    {
                        numMERRA2Nodes = ctx.MERRA_Node_table.Count();
                        ctx.Database.Connection.Close();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.InnerException.ToString());
                    return;
                }

                minId = 1;
                MERRA_Node_table[] merraDB = new MERRA_Node_table[20000];
                if (numMERRA2Nodes > 20000)
                    maxId = minId + 19999;
                else
                {
                    maxId = numMERRA2Nodes;
                    merraDB = new MERRA_Node_table[numMERRA2Nodes];
                }

                gotThemAll = false;

                while (numMERRA2Nodes > 0 && gotThemAll == false)
                {
                    textForProgBar = "Saving MERRA2 data...";
                    int prog = (int)(100 * (double)maxId / numMERRA2Nodes);
                    BackgroundWorker_SaveAs.ReportProgress(prog, textForProgBar);
                    int dataInd = 0;
                    // Copy up to 20000 entries from old DB

                    try
                    {
                        using (var ctx = new Continuum_EDMContainer(oldConnString))
                        {
                            var merra_exist_db = from N in ctx.MERRA_Node_table where N.Id >= minId && N.Id <= maxId select N;

                            foreach (var N in merra_exist_db)
                            {
                                merraDB[dataInd] = new MERRA_Node_table();
                                merraDB[dataInd].latitude = N.latitude;
                                merraDB[dataInd].longitude = N.longitude;
                                merraDB[dataInd].merraData = N.merraData;
                                dataInd++;
                            }
                            ctx.Database.Connection.Close();
                        }

                        using (var ctx = new Continuum_EDMContainer(newConnString))
                        {
                            ctx.MERRA_Node_table.AddRange(merraDB);
                            ctx.SaveChanges();
                            dataInd = 0;
                            merraDB = new MERRA_Node_table[20000];
                            ctx.Database.Connection.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.InnerException.ToString());
                        return;
                    }

                    if (maxId == numMERRA2Nodes)
                        gotThemAll = true;

                    minId = maxId + 1;
                    maxId = maxId + 20000;

                    if (maxId > numMERRA2Nodes)
                    {
                        maxId = numMERRA2Nodes;
                        merraDB = new MERRA_Node_table[maxId - minId + 1];
                    }
                }
            }
            else
            {
                // See if there is an existing DB and delete it if there is
                if (File.Exists(newFilename) == true)
                {
                    try
                    {
                        using (var ctx = new Continuum_EDMContainer(newConnString))
                        {
                            if (ctx.Database.Exists())
                                ctx.Database.Delete();
                            ctx.Database.Connection.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.InnerException.ToString());
                        return;
                    }

                }
            }

            DoWorkDone = true;
        }

        private void BackgroundWorker_SaveAs_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the 'Save As' progress bar
            string Text_for_label = e.UserState.ToString();
            if (e.ProgressPercentage <= 100)
                progbar.Value = e.ProgressPercentage;
            else
                progbar.Value = 100;

            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            Refresh();
        }

        private void BackgroundWorker_SaveAs_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Close();
        }

        /// <summary> Calls node surface roughness and displacement height (SRDH) recalculation background worker. (When land cover key changes) </summary>
        /// <param name="thisInst"></param>
        public void Call_BW_Node_Recalc(Continuum thisInst)
        {            
            Show();
            BackgroundWorker_Node_SR_Recalc.RunWorkerAsync(thisInst);
        }

        private void BackgroundWorker_Node_SR_Recalc_DoWork(object sender, DoWorkEventArgs e)
        {
            Continuum thisInst = (Continuum)e.Argument;

            // Recalculates SR/DH with new land cover key (or calculates SR/DH with newly imported land cover)
            Nodes nodeFromDB = new Nodes();
            NodeCollection nodeList = new NodeCollection();
            string connString = nodeList.GetDB_ConnectionString(thisInst.savedParams.savedFileName);

            int nodeCount = 0;
            string textForProgBar = "Recalculating SR/DH at Nodes in database";
            BackgroundWorker_Node_SR_Recalc.ReportProgress(0, textForProgBar);

            thisInst.topo.GetElevsAndSRDH_ForCalcs(thisInst, null, true);

            try
            {
                using (var context = new Continuum_EDMContainer(connString))
                {
                    var node_db = from N in context.Node_table.Include("expo") select N;
                    int totalCount = node_db.Count();

                    foreach (var N in node_db)
                    {
                        int numExpo = N.expo.Count();
                        nodeFromDB.UTMX = N.UTMX;
                        nodeFromDB.UTMY = N.UTMY;

                        nodeFromDB.expo = new Exposure[numExpo];

                        if (nodeCount % 10 == 0)
                        {
                            textForProgBar = "Recalculating SR/DH at Nodes in database. Node: " + nodeCount + "/" + totalCount;
                            BackgroundWorker_Node_SR_Recalc.ReportProgress(100 * nodeCount / totalCount, textForProgBar);
                        }

                        for (int i = 0; i < numExpo; i++)
                        {
                            nodeFromDB.expo[i] = new Exposure();
                            nodeFromDB.expo[i].radius = N.expo.ElementAt(i).radius;
                            nodeFromDB.expo[i].exponent = N.expo.ElementAt(i).exponent;
                            int numSectors = 1;
                            int smallerRadius = thisInst.topo.GetSmallerRadius(nodeFromDB.expo, nodeFromDB.expo[i].radius, nodeFromDB.expo[i].exponent, numSectors);

                            if (smallerRadius == 0 || numSectors > 1)
                            { // when sector avg is used, can//t add on to exposure calcs...so gotta do it the long way
                                if (thisInst.topo.gotSR == true)
                                    thisInst.topo.CalcSRDH(ref nodeFromDB.expo[i], nodeFromDB.UTMX, nodeFromDB.UTMY, nodeFromDB.expo[i].radius, nodeFromDB.expo[i].exponent, thisInst.metList.numWD);
                            }
                            else
                            {
                                Exposure smallerExpo = thisInst.topo.GetSmallerRadiusExpo(nodeFromDB.expo, smallerRadius, nodeFromDB.expo[i].exponent, numSectors);
                                if (thisInst.topo.gotSR == true)
                                    thisInst.topo.CalcSRDHwithSmallerRadius(ref nodeFromDB.expo[i], nodeFromDB.UTMX, nodeFromDB.UTMY, nodeFromDB.expo[i].radius, nodeFromDB.expo[i].exponent, numSectors, smallerRadius, smallerExpo, thisInst.metList.numWD);

                            }

                            // Write back to expo_db
                            MemoryStream MS7 = new MemoryStream();
                            MemoryStream MS8 = new MemoryStream();
                            BinaryFormatter bin = new BinaryFormatter();

                            if (nodeFromDB.expo[i].SR != null)
                            {
                                bin.Serialize(MS7, nodeFromDB.expo[i].SR);
                                N.expo.ElementAt(i).SR_Array = MS7.ToArray();
                            }

                            if (nodeFromDB.expo[i].dispH != null)
                            {
                                bin.Serialize(MS8, nodeFromDB.expo[i].dispH);
                                N.expo.ElementAt(i).DH_Array = MS8.ToArray();
                            }
                        }

                        nodeCount++;
                    }

                    // Save DB
                    context.SaveChanges();                    
                    
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.InnerException.ToString());
                return;
            }

            DoWorkDone = true;
        }

        private void BackgroundWorker_Node_SR_Recalc_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates progress bar of node SRDH recalculation

            string Text_for_label = e.UserState.ToString();
            if (e.ProgressPercentage <= 100)
                progbar.Value = e.ProgressPercentage;
            else
                progbar.Value = 100;

            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            BringToFront();
            Refresh();
        }

        private void BackgroundWorker_Node_SR_Recalc_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Close();
        }

        /// <summary> Calls WRG file creation background worker </summary>        
        public void Call_BW_WRG_create(Vars_for_WRG_file Args)
        {            
            Show();
            BackgroundWorker_WRG.RunWorkerAsync(Args);

        }

        private void BackgroundWorker_WRG_DoWork(object sender, DoWorkEventArgs e)
        {
            // Generates WRG (Wind Resource Grid) file based on selected map  
            Vars_for_WRG_file theArgs = (Vars_for_WRG_file)(e.Argument);
            Continuum thisInst = theArgs.thisInst;
            MetCollection metList = thisInst.metList;
            MapCollection mapList = thisInst.mapList;
            string mapName = theArgs.mapName;           
            double density = theArgs.density;     
            string outputFile = theArgs.outputFile;     
            TopoInfo topo = thisInst.topo;
            double[] windRose = metList.GetAvgWindRose(thisInst.modeledHeight, Met.TOD.All, Met.Season.All);
            Map thisMap = new Map();
     
            if (mapList.ThisCount > 0)
            {
                for (int i = 0; i < mapList.ThisCount; i++)
                {
                    if (mapName == mapList.mapItem[i].mapName)
                    {  // Found map
                        thisMap = mapList.mapItem[i];

                        if (thisMap.modelType != 2 && thisMap.modelType != 4)
                        {
                            if (thisMap.isWaked == false)
                            {
                                string modelType = "";
                                if (thisMap.modelType == 0)
                                    modelType = "UW Exposure";
                                else if (thisMap.modelType == 1)
                                    modelType = "DW Exposure";
                                else if ((thisMap.modelType == 3 || thisMap.modelType == 5) && thisMap.isWaked == false)
                                    modelType = "Energy Production";

                                MessageBox.Show("WRG maps are created using WS maps.  Please select a WS map and not a map of " + modelType);
                                return;
                            }
                        }

                        break;
                    }
                }
            }
            else
            {
                MessageBox.Show("No maps defined.", "Continuum 3");
                return;
            }                        

            StreamWriter sw = new StreamWriter(outputFile);

            double weibull_A_10;
            double weibull_k_100;

            int numX = thisMap.numX;
            int numY = thisMap.numY;
            double minUTMX = thisMap.minUTMX;
            double minUTMY = thisMap.minUTMY;
            int reso = thisMap.reso;

            string numX_String = numX.ToString();
            numX_String = numX_String.PadRight(8);

            string numY_String = numY.ToString();
            numY_String = numY_String.PadRight(8);

            string Min_UTMX_Str = minUTMX.ToString();
            Min_UTMX_Str = Min_UTMX_Str.PadRight(8);

            string Min_UTMY_Str = minUTMY.ToString();
            Min_UTMY_Str = Min_UTMY_Str.PadRight(8);

            string resoString = reso.ToString();

            sw.Write(numX_String);
            sw.Write(numY_String);
            sw.Write(Min_UTMX_Str);
            sw.Write(Min_UTMY_Str);
            sw.Write(resoString);
            sw.WriteLine();

         //   double[] power = null;

         //   if (turbList.PowerCurveCount > 0)
         //       power = turbList.powerCurves[0].power;

            int numPoints = numX * numY;
            int pointInd = 0;
            string[] metsUsed = metList.GetMetsUsed();                      

            // Get elevs for calcs
            topo.GetElevsAndSRDH_ForCalcs(thisInst, thisMap, false);

            for (int xind = 0; xind <= numX - 1; xind++)
            {
                double thisX = thisMap.minUTMX + xind * thisMap.reso;
            //    double minY = thisMap.minUTMY;
            //    double maxY = thisMap.minUTMY + (numY - 1) * thisMap.reso;

                for (int yind = 0; yind <= numY - 1; yind++)
                {
                    if (BackgroundWorker_WRG.CancellationPending == true)
                    {
                        sw.Close();
                        return;
                    }

                    double thisY = thisMap.minUTMY + yind * thisMap.reso;
                    pointInd++;
                    BackgroundWorker_WRG.ReportProgress(100 * pointInd / numPoints);

                    string easting = thisX.ToString();
                    easting = easting.PadRight(10);

                    string northing = thisY.ToString();
                    northing = northing.PadRight(10);

                    string elev = Math.Round(topo.CalcElevs(thisX, thisY), 2).ToString();
                    elev = elev.PadRight(8);

                    string heightString = thisInst.modeledHeight.ToString();
                    heightString = heightString.PadRight(5);

                    int numWS = metList.numWS;
                    int numWD = metList.numWD;
                    double[,] thisSectDist = new double[numWD, numWS];
                    double[] thisWindRose = metList.GetInterpolatedWindRose(metsUsed, thisX, thisY, Met.TOD.All, Met.Season.All, thisInst.modeledHeight);

                    for (int WD = 0; WD <= numWD - 1; WD++)
                    {
                        double[] This_WS_Dist = metList.CalcWS_DistForTurbOrMap(metsUsed, thisMap.sectorParamToMap[xind, yind, WD], WD, Met.TOD.All, Met.Season.All, thisInst.modeledHeight);

                        for (int WS = 0; WS <= numWS - 1; WS++)
                            thisSectDist[WD, WS] = This_WS_Dist[WS] * 1000;
                    }

                    double[] thisDist = metList.CalcOverallWS_Dist(thisSectDist, thisWindRose);
                    MetCollection.Weibull_params weibull = metList.CalcWeibullParams(thisDist, thisSectDist, thisMap.parameterToMap[xind, yind]);

                    string weibull_k_string = Math.Round(weibull.overall_k, 3).ToString();
                    weibull_k_string = weibull_k_string.PadRight(6);

                    string weibull_A_string = Math.Round(weibull.overall_A, 2).ToString();
                    weibull_A_string = weibull_A_string.PadRight(5);

                    double powerDensity = 0.5f * density * Math.Pow(thisMap.parameterToMap[xind, yind], 3);
                    string powerDensityString = Math.Round(powerDensity, 4).ToString();
                    powerDensityString = powerDensityString.PadRight(15);

                    string numSectorsString = numWD.ToString();
                    numSectorsString = numSectorsString.PadRight(3);

                    sw.Write("GridPoint ");
                    sw.Write(easting);
                    sw.Write(northing);
                    sw.Write(elev);
                    sw.Write(heightString);
                    sw.Write(weibull_A_string);
                    sw.Write(weibull_k_string);
                    sw.Write(powerDensityString);
                    sw.Write(numSectorsString);

                    for (int k = 0; k <= numWD - 1; k++)
                    {

                        string freqString = Math.Round(windRose[k] * 1000, 0).ToString();
                        freqString = freqString.PadRight(4);
                        sw.Write(freqString);

                        if (weibull.sector_A != null)
                            weibull_A_10 = weibull.sector_A[k] * 10;
                        else
                            weibull_A_10 = 0;

                        string weibull_A_10_string = Math.Round(weibull_A_10, 0).ToString();
                        weibull_A_10_string = weibull_A_10_string.PadRight(4);
                        sw.Write(weibull_A_10_string);

                        if (weibull.sector_k != null)
                            weibull_k_100 = weibull.sector_k[k] * 100;
                        else
                            weibull_k_100 = 0;

                        string Weibull_k_100_str = Math.Round(weibull_k_100, 0).ToString();
                        Weibull_k_100_str = Weibull_k_100_str.PadRight(5);
                        sw.Write(Weibull_k_100_str);
                    }

                    sw.WriteLine();
                }
            }

            sw.Close();
            DoWorkDone = true;
        }

        private void BackgroundWorker_WRG_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates progress bar of WRG file background worker
            string Text_for_label = "Creating WRG file...";

            if (e.ProgressPercentage <= 100)
                progbar.Value = e.ProgressPercentage;
            else
                progbar.Value = 100;

            Text = "Continuum 3";
            lblprogbar.Text = Text_for_label;
            Refresh();
        }

        private void BackgroundWorker_WRG_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Closes background worker after WRG file complete
            if (e.Cancelled)
                Close();
            else
                Close();
        }
               
        private void btnCancel_Click(object sender, EventArgs e)
        {
            // Cancels background worker and closes progress bar           

            if (BackgroundWorker_Map.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel this map generation?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_Map.CancelAsync();
            }
            else if (BackgroundWorker_TurbCalcs.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the turbine estimate calculations?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_TurbCalcs.CancelAsync();
            }
            else if (BackgroundWorker_RoundRobin.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the round robin uncertainty analysis?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_RoundRobin.CancelAsync();
            }
            else if (BackgroundWorker_MetCalcs.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel this met exposure and statistics calculation?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_MetCalcs.CancelAsync();
            }
            else if (BackgroundWorker_Topo.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the topo data import?  All data loaded in so far will be removed from database.", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_Topo.CancelAsync();
            }
            else if (BackgroundWorker_WAsP_Map.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the roughness data import?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_WAsP_Map.CancelAsync();
            }
            else if (BackgroundWorker_MERRA.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the MERRA2 data import?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_MERRA.CancelAsync();
            }
            else if (BackgroundWorker_Shadow.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the shadow flicker calculations?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_Shadow.CancelAsync();
            }
            else if (BackgroundWorker_IceThrow.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the ice throw model calculations?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_IceThrow.CancelAsync();
            }
            else if (BackgroundWorker_Exceed.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the Monte Carlo model calculations?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_Exceed.CancelAsync();
            }
            else if (BackgroundWorker_LandCover.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the land cover / surface roughness import?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_LandCover.CancelAsync();
            }
            else if (BackgroundWorker_MERRADownload.IsBusy == true)
            {
                DialogResult Yes_or_no = MessageBox.Show("Are you sure that you want to cancel the MERRA2 download?", "Continuum 3", MessageBoxButtons.YesNo);
                if (Yes_or_no == DialogResult.Yes)
                    BackgroundWorker_MERRADownload.CancelAsync();
            }

        }

        /// <summary> Checks to see if BackgroundWorkers are currently running  </summary>
        /// <returns> True if background worker is busy</returns>
        public bool IsBusy()
        {            
            bool Busy = false;
                       
            if (BackgroundWorker_LandCover.IsBusy || BackgroundWorker_Map.IsBusy || BackgroundWorker_MetCalcs.IsBusy || BackgroundWorker_Node_SR_Recalc.IsBusy
                || BackgroundWorker_RoundRobin.IsBusy || BackgroundWorker_SaveAs.IsBusy || BackgroundWorker_Topo.IsBusy || BackgroundWorker_TurbCalcs.IsBusy
                || BackgroundWorker_WAsP_Map.IsBusy || BackgroundWorker_WRG.IsBusy || BackgroundWorker_MERRA.IsBusy || BackgroundWorker_IceThrow.IsBusy
                || BackgroundWorker_Exceed.IsBusy || BackgroundWorker_Shadow.IsBusy)
                Busy = true;                        

            return Busy;
        }

        /// <summary> Calls MERRA2 import background worker (extracts required data from local MERRA2 files) </summary>        
        public void Call_BW_MERRA2_Import(Vars_for_MERRA vars_For_MERRA)
        {            
            Show();
            BackgroundWorker_MERRA.RunWorkerAsync(vars_For_MERRA);
        }

        private void BackgroundWorker_MERRA_DoWork(object sender, DoWorkEventArgs e)
        {
            // Reads in MERRA2 text files, interpolates to estimate MERRA2 data at specified lat/long
            Vars_for_MERRA theArgs = (Vars_for_MERRA)(e.Argument);
            Continuum thisInst = theArgs.thisInst;
            MERRA thisMERRA = theArgs.thisMERRA;
            MERRA.MERRA_Pull[] nodesToPull = theArgs.nodesToPull;
            int numNodesToPull = nodesToPull.Length;
            MERRACollection merraList = thisInst.merraList;
            Met thisMet = theArgs.thisMet;
            string MCP_Method = theArgs.MCP_type;

            int last_file_ind = 0;
            char[] delims = { ',', '\n' };

            string[] MERRAfiles = Directory.GetFiles(merraList.MERRAfolder, "*.ascii");

            StreamReader file;
            DateTime startTime = thisInst.dateMERRAStart.Value;
            DateTime endTime = thisInst.dateMERRAEnd.Value;
            // Convert start and end time to UTC
            startTime = startTime.AddHours(-thisMERRA.interpData.UTC_offset);
            endTime = endTime.AddHours(-thisMERRA.interpData.UTC_offset);

            // Set hour of startTime and endTime to 0. (Daily files are read in and all data is processed. After 
            // all files have been read, the data is trimmed to the actual start and end times
            DateTime startTimeZeroHour = startTime.AddHours(-startTime.Hour);
            DateTime endTimeZeroHour = endTime.AddHours(-endTime.Hour);
         //   endTimeZeroHour = endTimeZeroHour.AddDays(1); // Adding one more day to account for UTC - Local time diffs

            TimeSpan timeSpan = endTimeZeroHour - startTimeZeroHour;
            int numRefHours = Convert.ToInt32(timeSpan.TotalHours) + 24;

            // Resize MERRApull objects to hold all TS data and define East_North_WS arrays
            MERRA.East_North_WSs[] nodeEastNorthWS = new MERRA.East_North_WSs[numNodesToPull];

            for (int i = 0; i < numNodesToPull; i++)
            {
                Array.Resize(ref nodesToPull[i].Data, numRefHours);
                Array.Resize(ref nodeEastNorthWS[i].timeStamp, numRefHours);
                Array.Resize(ref nodeEastNorthWS[i].U10, numRefHours);
                Array.Resize(ref nodeEastNorthWS[i].U50, numRefHours);
                Array.Resize(ref nodeEastNorthWS[i].V10, numRefHours);
                Array.Resize(ref nodeEastNorthWS[i].V50, numRefHours);
            }

            int lastInd = 0;
            int counter = 0;

            // Looping through every day in specified date range
            for (DateTime thisDate = startTimeZeroHour; thisDate <= endTimeZeroHour; thisDate = thisDate.AddDays(1))
            {
                string datestring = thisMERRA.Make_MERRA2_Date_String(thisDate);
                counter++;
                TimeSpan Num_Days_Processed = thisDate.Subtract(startTime);
                TimeSpan Num_Days_Total = endTime.Subtract(startTime);
                double Prog = 100 * Num_Days_Processed.TotalDays / Num_Days_Total.TotalDays;

                if (counter % 50 == 0)
                    BackgroundWorker_MERRA.ReportProgress((int)Prog, "Importing MERRA2 data");

                if (BackgroundWorker_MERRA.CancellationPending == true)
                {
                    if (thisMet != null)
                        thisMet.CalcAllMeas_WSWD_Dists(thisInst, thisMet.metData.GetSimulatedTimeSeries(thisInst.modeledHeight));
                    e.Result = thisInst;
                    return;
                }

                // Looping through every file to find correct file by matching with datestring
                for (int i = last_file_ind; i < MERRAfiles.Length; i++)
                {
                    string thisFile = MERRAfiles[i];

                    if (thisFile.Substring(thisFile.Length - 18, 8) == datestring)
                    {
                        try
                        {
                            file = new StreamReader(thisFile);
                            last_file_ind = i + 1;
                        }
                        catch
                        {
                            MessageBox.Show("Error opening the MERRA data file. Check that it's not open in another program.", "", MessageBoxButtons.OK);
                            return;
                        }

                        string file_contents = file.ReadToEnd();
                        string[] file_strings = file_contents.Split(delims);
                        int file_ind = 1; // skip header file_string
                        int Col_count = 1;

                        while (file_ind < file_strings.Length && file_strings[file_ind - 1] != "time")
                        {
                            if (thisMERRA.Need_This_Param(file_strings[file_ind]))
                            {
                                // Using location of brackets in file line to grab the hour of that line
                                int open_bracket = file_strings[file_ind].IndexOf("[");
                                int close_bracket = file_strings[file_ind].IndexOf("]");

                                int ThisHour = Convert.ToInt16(file_strings[file_ind].Substring(open_bracket + 1, close_bracket - open_bracket - 1));
                                int thisInd = lastInd + ThisHour;
                         //       DateTime thisTimeStamp = thisDate.AddHours(ThisHour);

                                open_bracket = file_strings[file_ind].LastIndexOf("[");
                                close_bracket = file_strings[file_ind].LastIndexOf("]");
                                int This_X_ind = Convert.ToInt16(file_strings[file_ind].Substring(open_bracket + 1, close_bracket - open_bracket - 1));

                                for (int j = 0; j < nodesToPull.Length; j++)
                                {
                                    if (This_X_ind == nodesToPull[j].XY_ind.X_ind)
                                    {

                                        if (file_strings[file_ind].Substring(0, 3) == "U50")
                                            nodeEastNorthWS[j].U50[thisInd] = Convert.ToSingle(file_strings[file_ind + 1 + nodesToPull[j].XY_ind.Y_ind]);
                                        else if (file_strings[file_ind].Substring(0, 3) == "V50")
                                            nodeEastNorthWS[j].V50[thisInd] = Convert.ToSingle(file_strings[file_ind + 1 + nodesToPull[j].XY_ind.Y_ind]);
                                        else if (file_strings[file_ind].Substring(0, 3) == "U10")
                                            nodeEastNorthWS[j].U10[thisInd] = Convert.ToSingle(file_strings[file_ind + 1 + nodesToPull[j].XY_ind.Y_ind]);
                                        else if (file_strings[file_ind].Substring(0, 3) == "V10")
                                            nodeEastNorthWS[j].V10[thisInd] = Convert.ToSingle(file_strings[file_ind + 1 + nodesToPull[j].XY_ind.Y_ind]);
                                        else if (file_strings[file_ind].Substring(0, 4) == "T10M")
                                            nodesToPull[j].Data[thisInd].Temp10m = Convert.ToSingle(file_strings[file_ind + 1 + nodesToPull[j].XY_ind.Y_ind]);
                                        else if (file_strings[file_ind].Substring(0, 2) == "PS")
                                            nodesToPull[j].Data[thisInd].SurfPress = Convert.ToSingle(file_strings[file_ind + 1 + nodesToPull[j].XY_ind.Y_ind]);
                                        else if (file_strings[file_ind].Substring(0, 3) == "SLP")
                                            nodesToPull[j].Data[thisInd].SeaPress = Convert.ToSingle(file_strings[file_ind + 1 + nodesToPull[j].XY_ind.Y_ind]);

                                        // Save dates in local time
                                        nodesToPull[j].Data[thisInd].ThisDate = thisDate.AddHours(ThisHour + thisMERRA.interpData.UTC_offset);
                                        nodeEastNorthWS[j].timeStamp[thisInd] = thisDate.AddHours(ThisHour + thisMERRA.interpData.UTC_offset);
                                    }
                                }
                            }

                            if (Col_count == 1)
                            {
                                file_ind++; // go to first data point
                                while (file_strings[file_ind].Contains("[") == false)
                                {
                                    file_ind++;
                                    Col_count++;
                                }
                            }
                            else
                                file_ind = file_ind + Col_count;
                        }


                        file.Close();
                        break; // exiting for loop cycling through files, this break only reached if the correct file has been found and read in
                    }
                }

                // update lastInd
                while (nodeEastNorthWS[0].U50[lastInd] != 0 && lastInd < (numRefHours - 1))
                    lastInd++;
            }

            // Find index of start date (with the conversion b/w UTC and local, there will be some additional entries to trim)
            // Convert start and end time back to Local
            startTime = startTime.AddHours(thisMERRA.interpData.UTC_offset);
            endTime = endTime.AddHours(thisMERRA.interpData.UTC_offset);
            int startInd = 0;
            while (nodesToPull[0].Data[startInd].ThisDate < startTime)
                startInd++;

            if (startTime < nodesToPull[0].Data[startInd].ThisDate)
            {
                MessageBox.Show("Available MERRA2 data does not cover desired date range.", "Continuum 3");
                if (thisMet != null)
                    thisMet.CalcAllMeas_WSWD_Dists(thisInst, thisMet.metData.GetSimulatedTimeSeries(thisInst.modeledHeight));
                e.Result = thisInst;
                return;
            }

            int endInd = nodesToPull[0].Data.Length - 1;
            while (nodesToPull[0].Data[endInd].ThisDate > endTime)
                endInd--;

            // Get rid of any extra entries and calculates and populates MERRApull[] with WS & WD data at 50 & 10m (at each MERRA node) using U and V wind speeds            
            for (int i = 0; i < numNodesToPull; i++)
            {
                MERRA.Wind_TS[] croppedData = new MERRA.Wind_TS[endInd - startInd + 1];
                Array.Copy(nodesToPull[i].Data, startInd, croppedData, 0, endInd - startInd + 1);
                nodesToPull[i].Data = croppedData;

                MERRA.East_North_WSs[] croppedEastNorth = new MERRA.East_North_WSs[1];
                croppedEastNorth[0].U10 = new double[endInd - startInd + 1];
                croppedEastNorth[0].U50 = new double[endInd - startInd + 1];
                croppedEastNorth[0].V10 = new double[endInd - startInd + 1];
                croppedEastNorth[0].V50 = new double[endInd - startInd + 1];

                Array.Copy(nodeEastNorthWS[i].U10, startInd, croppedEastNorth[0].U10, 0, endInd - startInd + 1);
                nodeEastNorthWS[i].U10 = croppedEastNorth[0].U10;

                Array.Copy(nodeEastNorthWS[i].U50, startInd, croppedEastNorth[0].U50, 0, endInd - startInd + 1);
                nodeEastNorthWS[i].U50 = croppedEastNorth[0].U50;

                Array.Copy(nodeEastNorthWS[i].V10, startInd, croppedEastNorth[0].V10, 0, endInd - startInd + 1);
                nodeEastNorthWS[i].V10 = croppedEastNorth[0].V10;

                Array.Copy(nodeEastNorthWS[i].V50, startInd, croppedEastNorth[0].V50, 0, endInd - startInd + 1);
                nodeEastNorthWS[i].V50 = croppedEastNorth[0].V50;

                thisMERRA.Calc_MERRA2_WS_WD(ref nodesToPull, nodeEastNorthWS);
            }

            // Check that MERRA data was read in
            bool gotItAll = thisMERRA.WasDataFullyLoaded(nodesToPull);
            if (gotItAll == false)
            {
                if (thisMet != null)
                    thisMet.CalcAllMeas_WSWD_Dists(thisInst, thisMet.metData.GetSimulatedTimeSeries(thisInst.modeledHeight));
                e.Result = thisInst;
                return;
            }

            // Add new MERRA nodes to database
            thisMERRA.AddNewDataToDB(thisInst, nodesToPull);

            // Add MERRA object to list
            Array.Resize(ref thisInst.merraList.merraData, thisInst.merraList.numMERRA_Data + 1);
            thisInst.merraList.merraData[thisInst.merraList.numMERRA_Data - 1] = thisMERRA;

            // Get all nodes from database
            thisMERRA.GetMERRADataFromDB(thisInst);

            // Generate interpData
            thisMERRA.GetInterpData(thisInst.UTM_conversions);

            thisMERRA.Calc_MonthProdStats(thisInst.UTM_conversions);
            thisMERRA.CalcAnnualProd(ref thisMERRA.interpData.Annual_Prod, thisMERRA.interpData.Monthly_Prod, thisInst.UTM_conversions);

            // Runs MCP at met sites if MCP_type not null
            if (theArgs.MCP_type != null && theArgs.thisMet.name != null)
            {
                thisMet.mcp = new MCP();
                thisMet.WSWD_Dists = new Met.WSWD_Dist[0];
                thisInst.metList.RunMCP(ref thisMet, thisMERRA, thisInst, MCP_Method);  // Runs MCP and generates LT WS estimates                   
                thisMet.CalcAllLT_WSWD_Dists(thisInst, thisMet.mcp.LT_WS_Ests); // Calculates LT wind speed / wind direction distributions for using all day and using each season and each time of day (Day vs. Night)                               

                thisInst.metList.isMCPd = true;

                // Checks to see if all mets have MCP estimates and sets metList.allMCPd boolean
                thisInst.metList.AreAllMetsMCPd();
            }

            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_MERRA_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates all met lists, turbine calcs, model plots etc on main form 
            if (e.Cancelled == false)
            {
                Continuum thisInst = (Continuum)e.Result;
                if (thisInst.IsDisposed == false)
                {
                    thisInst.ChangesMade();
                    updateThe.AllTABs(thisInst);
                }                
            }

            Close();
        }

        private void BackgroundWorker_MERRA_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the MERRA2 import progress bar
            BringToFront();
            string textForLabel = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = textForLabel;
            Refresh();
        }

        /// <summary>  Calls Ice Throw model background worker </summary>
        /// <param name="varsForBW"></param>
        public void Call_BW_IceThrow(Vars_for_BW varsForBW)
        {             
            Show();
            BackgroundWorker_IceThrow.RunWorkerAsync(varsForBW);
        }

        private void BackgroundWorker_IceThrow_DoWork(object sender, DoWorkEventArgs e)
        {
            // Runs ice throw model
            DoWorkDone = false;
            Vars_for_BW theArgs = (Vars_for_BW)(e.Argument);
            Continuum thisInst = theArgs.thisInst;

            Met_Data_Filter metDataFilter = new Met_Data_Filter();
            TurbineCollection.PowerCurve powerCurve = thisInst.GetSelectedPowerCurve("Site Suitability");

            SiteSuitability siteSuit = thisInst.siteSuitability;

            siteSuit.yearlyIceHits = new SiteSuitability.YearlyHits[siteSuit.numYearsToModel];
            Random thisRandom = siteSuit.GetRandomNumber();

            int numYearlyHits = siteSuit.iceThrowsPerIceDay * siteSuit.numIceDaysPerYear;
            int totalIceHits = numYearlyHits * thisInst.turbineList.TurbineCount; // Only used to calculate progress for progress bar
            int totalCount = 0;

            //     string fileName = "C:\\Users\\OEE2017_32\\Dropbox (OEE)\\Software - Development\\Continuum\\v3.0\\QA Backup files\\Testing 3.0\\Site Suitability\\Ice Throw\\Ice Throw Hits Findlay Turbines.txt";
            //     StreamWriter file = new StreamWriter(fileName);

            for (int y = 0; y < siteSuit.numYearsToModel; y++)
            {
                siteSuit.yearlyIceHits[y].iceHits = new SiteSuitability.FinalPosition[totalIceHits];
                int iceHitCount = 0;

                for (int i = 0; i < thisInst.turbineList.TurbineCount; i++)
                {
                    Turbine thisTurbine = thisInst.turbineList.turbineEsts[i];
                    Turbine.Avg_Est thisAvgEst = thisTurbine.GetAvgWS_Est(null);
                    double[,] sectorDists = thisAvgEst.freeStream.sectorWS_Dist;
                    double[] windRose = thisAvgEst.freeStream.windRose;

                    double Prog = 100 * (y * thisInst.turbineList.TurbineCount + i) / (thisInst.turbineList.TurbineCount * siteSuit.numYearsToModel);

                    BackgroundWorker_IceThrow.ReportProgress((int)Prog, "Running Ice Throw Model");

                    if (thisAvgEst.freeStream.sectorWS_Dist == null) // Use closest met site instead
                    {
                        Met thisMet = thisInst.metList.GetClosestMet(thisTurbine.UTMX, thisTurbine.UTMY);
                        Met.WSWD_Dist thisDist = thisMet.GetWS_WD_Dist(thisInst.modeledHeight, Met.TOD.All, Met.Season.All);
                        sectorDists = thisDist.sectorWS_Dist;
                        windRose = thisDist.windRose;
                    }

                    if (sectorDists == null || windRose == null)
                        return;

                    double[,] theseCDFs = siteSuit.GenerateWS_CDFs(sectorDists);

                    int numWD = thisInst.metList.numWD;

                    //  for (int j = 0; j < GetNumZones(); j++)  // IT DOESN'T MAKE SENSE TO MODEL DIFFERENT NUMBER OF ICE THROWS BASED ON NUMBER OF ZONES. The zone is only used to determine
                    //  the difference in elevation. Since ice throw doesn't typically travel more than ~500 m, it is okay to assume that the elevation of the turbine and zone are approx. equal
                    //  {
                    for (int WD = 0; WD < 360; WD++)
                    {
                        // Loop through each wind direction by n iterations = Wind_Rose[]/(Wind_Rose bin size) * Num Ice Throw events
                        int WD_Ind = metDataFilter.GetWD_Ind(WD, numWD);
                        int numIters = Convert.ToInt16(windRose[WD_Ind] / (360 / numWD) * numYearlyHits);

                        if (BackgroundWorker_IceThrow.CancellationPending == true)
                        {
                            thisInst.siteSuitability.ClearIceThrow();
                            e.Result = thisInst;
                            return;
                        }

                        for (int n = 0; n < numIters; n++)
                        {
                            double[] thisCDF = siteSuit.GetOneWS_CDF(theseCDFs, WD_Ind);
                            double thisRand = thisRandom.NextDouble();
                            double thisWS = siteSuit.FindCDF_WS(thisCDF, thisRand, thisInst.metList);

                            double tipSpeed = siteSuit.GetTipSpeed(powerCurve, thisWS);

                            thisRand = thisRandom.NextDouble();
                            double randRadius = siteSuit.GetRandomRadius(thisRand, powerCurve);
                            double iceSpeed = tipSpeed * randRadius / (powerCurve.RD / 2);

                            thisRand = thisRandom.NextDouble();
                            int degrees = siteSuit.GetDegrees(thisRand);

                            thisRand = thisRandom.NextDouble();
                            double iceMass = siteSuit.GetIceMass(thisRand);

                            thisRand = thisRandom.NextDouble();
                            double iceShape = siteSuit.GetIceShape(thisRand);
                            double iceCrossSecArea = siteSuit.GetIceCrossSecArea(iceMass, iceShape);
                            double elevDiff = 0;
                            double Cd = siteSuit.GetCd(iceShape);

                            if (iceHitCount >= totalIceHits) // Due to rounding, there may be a few more or a few less than 3000 throws
                                Array.Resize(ref siteSuit.yearlyIceHits[y].iceHits, iceHitCount + 1);

                            siteSuit.yearlyIceHits[y].iceHits[iceHitCount] = siteSuit.ModelIceThrow(degrees, thisInst.modeledHeight, elevDiff, randRadius, iceSpeed, Cd, iceCrossSecArea, iceMass, thisWS, WD, thisTurbine);
                            iceHitCount++;
                            totalCount++;

                        }
                    }
                }

                Array.Resize(ref siteSuit.yearlyIceHits[y].iceHits, iceHitCount);

                //      file.WriteLine("Turbine, UTMX, UTMY");

                //      file.WriteLine("Year " + y + 1);
                //      for (int j = 0; j < siteSuit.yearlyIceHits[y].iceHits.Length; j++)
                //          file.WriteLine(siteSuit.yearlyIceHits[y].iceHits[j].turbineName + "," + Math.Round(siteSuit.yearlyIceHits[y].iceHits[j].thisX, 2)
                //              + "," + Math.Round(siteSuit.yearlyIceHits[y].iceHits[j].thisZ, 2));


                //       file.WriteLine();

            }

            //  file.Close();
            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_IceThrow_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the ice throw progress bar
            BringToFront();
            string textForLabel = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = textForLabel;
            Refresh();
        }

        private void BackgroundWorker_IceThrow_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates site suitability form 
            Continuum thisInst = (Continuum)e.Result;

            if (thisInst.IsDisposed == false)
            {
                thisInst.updateThe.SiteSuitabilityDropdown(thisInst, "Ice Throw");
                thisInst.updateThe.IcingYearsDropDown(thisInst);
                thisInst.updateThe.SiteSuitabilityTAB(thisInst);
                thisInst.ChangesMade();

                thisInst.updateThe.ColoredButtons(thisInst);
            }

            Close();
        }

        /// <summary>  Calls Shadow Flicker model background worker </summary>               
        public void Call_BW_Shadow(Vars_for_BW varsForBW)
        {            
            Show();
            BackgroundWorker_Shadow.RunWorkerAsync(varsForBW);
        }

        private void BackgroundWorker_Shadow_DoWork(object sender, DoWorkEventArgs e)
        {           
            Vars_for_BW theArgs = (Vars_for_BW)(e.Argument);
            Continuum thisInst = theArgs.thisInst;
            SiteSuitability siteSuit = thisInst.siteSuitability;
            
            DateTime dateTime = new DateTime(2019, 1, 1);
            DateTime stopTime = new DateTime(2020, 1, 1);
            DateTime lastDay = dateTime;
            int[] totalDailyMins = new int[siteSuit.GetNumZones()];
            int UTC_offset = thisInst.UTM_conversions.GetUTC_Offset(siteSuit.flickerMap[0].latitude, siteSuit.flickerMap[0].longitude);
            int count = 0;
            DateTime[] sunRiseAndSet = siteSuit.GetSunriseAndSunsetTimes(siteSuit.flickerMap[0].latitude, siteSuit.flickerMap[0].longitude, UTC_offset, dateTime);
            //     SiteSuitability.FlickerAngles minFlickerAngles = siteSuit.FindMinSolarAngles(thisInst); // not quite working. taking out for now

            while (dateTime < stopTime)
            {             
                if (dateTime.Day != lastDay.Day)
                {
                    // Calculate sunrise and sunset time
                    sunRiseAndSet = siteSuit.GetSunriseAndSunsetTimes(siteSuit.flickerMap[0].latitude, siteSuit.flickerMap[0].longitude, UTC_offset, dateTime);

                    // Update max daily shadow if higher than previously found maxDailyShadowMins
                    for (int z = 0; z < siteSuit.GetNumZones(); z++)
                        if (totalDailyMins[z] > siteSuit.zones[z].flickerStats.maxDailyShadowMins)
                        {
                            siteSuit.zones[z].flickerStats.maxDailyShadowMins = totalDailyMins[z];
                            siteSuit.zones[z].flickerStats.maxShadowDay = lastDay;
                        }

                    totalDailyMins = new int[siteSuit.GetNumZones()];
                    lastDay = dateTime;
                }

                // Only check sun position if time is between 6 am and 10 pm.
                if (dateTime.Hour >= sunRiseAndSet[0].Hour && dateTime.Hour <= sunRiseAndSet[1].Hour)
                {
                    bool sunIsUp = siteSuit.isSunUp(dateTime, UTC_offset, siteSuit.flickerMap[0].latitude,
                        siteSuit.flickerMap[0].longitude);

                    if (sunIsUp)
                    {
                        SiteSuitability.SunPosition sunPosition = siteSuit.GetSunPosition(dateTime, UTC_offset, siteSuit.flickerMap[0].latitude, siteSuit.flickerMap[0].longitude);

                        if (sunPosition.altitude > 0)
                        {
                            // Checks the sun position compared to the min azimuth and altitude and max angle variance                    
                            if (sunPosition.isSunUp)
                            {
                                for (int z = 0; z < siteSuit.GetNumZones(); z++)
                                {
                                    // loop through zones and calculate flicker statistics
                                    for (int i = 0; i < thisInst.turbineList.TurbineCount; i++)
                                    {
                                        double aziDiff = sunPosition.azimuth - siteSuit.zones[z].flickerAngles[i].targetAzimuthAngle;
                                        double altDiff = sunPosition.altitude - siteSuit.zones[z].flickerAngles[i].targetAltitudeAngle;
                                        double angleErrorSqr = aziDiff * aziDiff + altDiff * altDiff;
                                        double angleVarSqr = siteSuit.zones[z].flickerAngles[i].angleVariance * siteSuit.zones[z].flickerAngles[i].angleVariance;
                                                                               
                                        if (sunPosition.isSunUp && (angleErrorSqr <= angleVarSqr))
                                        {
                                            int monthInd = dateTime.Month - 1;
                                            int hourInd = dateTime.Hour;
                                            siteSuit.zones[z].flickerStats.shadowMins12x24[monthInd, hourInd]++;
                                            siteSuit.zones[z].flickerStats.totalShadowMinsPerMonth[monthInd]++;
                                            siteSuit.zones[z].flickerStats.totalShadowMinsPerYear++;
                                            totalDailyMins[z]++;
                                            break; // A turbine causes flicker so exit loop so no double counting for other turbines that cause flicker
                                        }
                                    }
                                }

                                int flickerInd = 0;

                                for (int i = 0; i < siteSuit.numXFlicker; i++)
                                    for (int j = 0; j < siteSuit.numYFlicker; j++)
                                    {
                                        for (int t = 0; t < thisInst.turbineList.TurbineCount; t++)
                                        {
                                            double aziDiff = sunPosition.azimuth - siteSuit.flickerMap[flickerInd].flickerAngles[t].targetAzimuthAngle;
                                            double altDiff = sunPosition.altitude - siteSuit.flickerMap[flickerInd].flickerAngles[t].targetAltitudeAngle;
                                            double angleErrorSqr = aziDiff * aziDiff + altDiff * altDiff;
                                            double angleVarSqr = siteSuit.flickerMap[flickerInd].flickerAngles[t].angleVariance * siteSuit.flickerMap[flickerInd].flickerAngles[t].angleVariance;
                                                                                        
                                            if (sunPosition.isSunUp && (angleErrorSqr <= angleVarSqr))
                                            {
                                                int monthInd = dateTime.Month - 1;
                                                int hourInd = dateTime.Hour;
                                                siteSuit.flickerMap[flickerInd].flickerStats.shadowMins12x24[monthInd, hourInd]++;
                                                siteSuit.flickerMap[flickerInd].flickerStats.totalShadowMinsPerMonth[monthInd]++;
                                                siteSuit.flickerMap[flickerInd].flickerStats.totalShadowMinsPerYear++;
                                                break; // A turbine causes flicker so exit loop so no double counting for other turbines than cause flicker
                                            }
                                        }
                                        flickerInd++;
                                    }
                            }
                        }
                    }

                    count++;

                    if (count % 500 == 0)
                    {
                        double Prog = Math.Min(100 * count / (365.0 * 15.0 * 60.0), 100); // 365 days * 12 hours (estimated avg length from sunrise to sunset) * 60 minutes
                        BackgroundWorker_Shadow.ReportProgress((int)Prog, "Running Shadow Flicker Model");
                    }

                    if (BackgroundWorker_Shadow.CancellationPending == true)
                    {
                        thisInst.siteSuitability.ClearShadowFlicker();
                        e.Result = thisInst;
                        return;
                    }
                }

                dateTime = dateTime.AddMinutes(1);
            }

            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_Shadow_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the shadow flicker progress bar
            BringToFront();
            string textForLabel = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = textForLabel;
            Refresh();
        }

        private void BackgroundWorker_Shadow_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates site suitability form 
            Continuum thisInst = (Continuum)e.Result;
            if (thisInst.IsDisposed == false)
            {
                thisInst.updateThe.SiteSuitabilityDropdown(thisInst, "Shadow Flicker");
                thisInst.updateThe.SiteSuitabilityTAB(thisInst);
                thisInst.updateThe.ColoredButtons(thisInst);

                if (thisInst.siteSuitability.numXFlicker != 0)
                    thisInst.SaveFile(false);
            }

            Close();
        }

        /// <summary> Calls Monte Carlo exceedance background worker </summary>
        /// <param name="varsForBW"></param>
        public void Call_BW_Exceed(Vars_for_BW varsForBW)
        {            
            Show();
            BackgroundWorker_Exceed.RunWorkerAsync(varsForBW);
        }

        private void BackgroundWorker_Exceed_DoWork(object sender, DoWorkEventArgs e)
        {
            // Run Monte Carlo exceedance calculations

            // For each exceedance, generate a random number and read off corresponding PF
            // calculate totalPF for each simulation
            // Calculate the P values of totalPFs

            Vars_for_BW theArgs = (Vars_for_BW)(e.Argument);
            Continuum thisInst = theArgs.thisInst;
            Exceedance exceedance = thisInst.turbineList.exceed;
            int numSims = exceedance.numSims;
            
            int numYears = 1;
            int count = 0;
            int totalCount = numSims * exceedance.Num_Exceed() + numSims * exceedance.Num_Exceed() * 10 + numSims * exceedance.Num_Exceed() * 20;

            for (int yearInd = 0; yearInd < 3; yearInd++)
            {               
                Random thisRand = exceedance.GetRandomNumber();
                
                double[] totalPFs = new double[numSims];

                for (int i = 0; i < numSims; i++)
                {                    
                    double avgPF = 0;

                    double Prog = Math.Min(100 * (double)count / totalCount, 100);
                    if (i % 500 == 0)
                        BackgroundWorker_Exceed.ReportProgress((int)Prog, "Running Exceedance Monte Carlo Model");

                    if (BackgroundWorker_Exceed.CancellationPending == true)
                    {
                        e.Result = thisInst;
                        return;
                    }

                    for (int y = 0; y < numYears; y++)
                    {
                        double totalPF = 1;
                        for (int j = 0; j < exceedance.Num_Exceed(); j++)
                        {
                            double randNum = thisRand.NextDouble();
                            double thisPF = exceedance.Get_PF_Value(randNum, exceedance.exceedCurves[j]);
                            totalPF = totalPF * thisPF;                            
                            count++;                                                        
                        }
                        avgPF = avgPF + totalPF;
                    }

                    avgPF = avgPF / numYears;
                    totalPFs[i] = avgPF;
                }

                Array.Sort(totalPFs);
                /*
                if (yearInd == 0)
                {
                    thisInst.sfd60mWS.FileName = "C:\\Users\\OEE2017_32\\Dropbox (OEE)\\Software - Development\\Continuum\\v3.0\\QA Backup files\\Testing 3.0\\FindPValues\\totalPF_1yr.txt";
                    StreamWriter sw_1yr = new StreamWriter(thisInst.sfd60mWS.FileName);
                    for (int i = 0; i < numSims; i++)
                        sw_1yr.WriteLine(totalPFs[i]);

                    sw_1yr.Close();
                }
                else if (yearInd == 1)
                {
                    thisInst.sfd60mWS.FileName = "C:\\Users\\OEE2017_32\\Dropbox (OEE)\\Software - Development\\Continuum\\v3.0\\QA Backup files\\Testing 3.0\\FindPValues\\totalPF_10yrs.txt";
                    StreamWriter sw_10yr = new StreamWriter(thisInst.sfd60mWS.FileName);
                    for (int i = 0; i < numSims; i++)
                        sw_10yr.WriteLine(totalPFs[i]);

                    sw_10yr.Close();
                }
                else
                {
                    thisInst.sfd60mWS.FileName = "C:\\Users\\OEE2017_32\\Dropbox (OEE)\\Software - Development\\Continuum\\v3.0\\QA Backup files\\Testing 3.0\\FindPValues\\totalPF_20yrs.txt";
                    StreamWriter sw_20yr = new StreamWriter(thisInst.sfd60mWS.FileName);
                    for (int i = 0; i < numSims; i++)
                        sw_20yr.WriteLine(totalPFs[i]);

                    sw_20yr.Close();
                }
                */
                exceedance.FindPValues(totalPFs, numYears);

                if (yearInd == 0)
                    numYears = 10;
                else if (yearInd == 1)
                    numYears = 20;
            }

            exceedance.compositeLoss.isComplete = true;
            DoWorkDone = true;
            e.Result = thisInst;
        }

        private void BackgroundWorker_Exceed_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the exceedance progress bar
            BringToFront();
            string textForLabel = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = textForLabel;
            Refresh();
        }

        private void BackgroundWorker_Exceed_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Updates exceedance tab
            Continuum thisInst = (Continuum)e.Result;

            if (e.Cancelled == true)
            {
                thisInst.turbineList.exceed = new Exceedance();
                thisInst.turbineList.exceed.compositeLoss.isComplete = false;
            }
            else
            {
                thisInst.ChangesMade();
            }

            if (thisInst.IsDisposed == false)
                thisInst.updateThe.AllTABs(thisInst);

            Close();
        }

        /// <summary> Calls MERRA2 download background worker. Connects to NASA website and downloads daily files to local PC. </summary>        
        public void Call_BW_MERRA2_Download(Vars_for_BW vars_For_MERRA)
        {            
            Show();
            BackgroundWorker_MERRADownload.RunWorkerAsync(vars_For_MERRA);
        }

        private void BackgroundWorker_MERRADownload_DoWork(object sender, DoWorkEventArgs e)
        {
            Vars_for_BW theArgs = (Vars_for_BW)(e.Argument);
            Continuum thisInst = theArgs.thisInst;
            MERRACollection merraList = thisInst.merraList;

            string urs = "https://urs.earthdata.nasa.gov";
            CookieContainer myContainer = new CookieContainer();

            // Create a credential cache for authenticating when redirected to Earthdata Login

            CredentialCache cache = new CredentialCache();
            cache.Add(new Uri(urs), "Basic", new NetworkCredential(merraList.earthdataUser, merraList.earthdataPwd));   
            
            if (thisInst.txtMinLat.Text == "" || thisInst.txtMaxLat.Text == "" || thisInst.txtMinLong.Text == "" || thisInst.txtMaxLong.Text == "")
            {
                MessageBox.Show("Specify the latitude/longitude bounding box before clicking 'Download MERRA2'.  This will begin the download process of MERRA2 data files to the specified MERRA2 folder location");
                return;
            }

            double minLat = Convert.ToDouble(thisInst.txtMinLat.Text);
            double maxLat = Convert.ToDouble(thisInst.txtMaxLat.Text);
            double minLong = Convert.ToDouble(thisInst.txtMinLong.Text);
            double maxLong = Convert.ToDouble(thisInst.txtMaxLong.Text);

            minLat = Math.Round(minLat / 0.5, 0) * 0.5;
            maxLat = Math.Round(maxLat / 0.5, 0) * 0.5;
            minLong = Math.Round(minLong / 0.625) * 0.625;
            maxLong = Math.Round(maxLong / 0.625) * 0.625;

            int offset = thisInst.UTM_conversions.GetUTC_Offset(minLat, minLong);
            DateTime startDate = thisInst.dateMERRAStart.Value;
            startDate = startDate.AddHours(-offset);
            DateTime endDate = thisInst.dateMERRAEnd.Value;
            endDate = endDate.AddHours(-offset);

            DateTime startDayOnly = new DateTime(startDate.Year, startDate.Month, startDate.Day);
            DateTime endDayOnly = new DateTime(endDate.Year, endDate.Month, endDate.Day);

            int numDays = endDayOnly.Subtract(startDayOnly).Days + 1;

            List<int> integerList = Enumerable.Range(0, numDays).ToList();
            int count = 0;

            Stopwatch thisStopwatch = new Stopwatch();
            thisStopwatch.Start();

            double timeElapsed = 0;
            double avgTimePerFile = 0;
            double timeToFinish;

            // Attempt to connect and test credentials
            string testResource = merraList.GetMERRA2URL(startDate, minLat, maxLat, minLong, maxLong);
                       
            HttpWebRequest testRequest = (HttpWebRequest)WebRequest.Create(testResource);
            testRequest.Method = "GET";
            testRequest.Credentials = cache;
            testRequest.CookieContainer = myContainer;
            testRequest.PreAuthenticate = false;
            testRequest.AllowAutoRedirect = true;
            testRequest.Timeout = 100000;

            HttpWebResponse testResponse = null;

            try
            {
                testResponse = (HttpWebResponse)testRequest.GetResponse();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message.ToString());
                thisInst.merraList.earthdataUser = "";
                thisInst.merraList.earthdataPwd = "";
                e.Result = thisInst;
                return;
            }            

            Parallel.ForEach(integerList, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                DateTime thisDate = startDate.AddDays(i);
                bool fileExists = merraList.MERRA2FileExists(thisDate);

                double Prog = Math.Min(100 * (double)count / numDays, 100);
                if (count % 10 == 0)
                {
                    timeElapsed = (thisStopwatch.Elapsed.TotalSeconds - timeElapsed);
                    avgTimePerFile = (thisStopwatch.Elapsed.TotalSeconds / (count + 1));
                    timeToFinish = (numDays - count) * avgTimePerFile / 60;                    
                    BackgroundWorker_MERRADownload.ReportProgress((int)Prog, "Downloading MERRA2 data. Avg time/file: " + Math.Round(avgTimePerFile, 1) +
                        " secs. Est. time to finish: " + Math.Round(timeToFinish, 1) + " mins.");
                }                    

                if (BackgroundWorker_MERRADownload.CancellationPending == true)
                {
                    e.Result = thisInst;
                    return;
                }

                if (fileExists == false)
                {
                    // Execute the request
                    string resource = merraList.GetMERRA2URL(thisDate, minLat, maxLat, minLong, maxLong);
                                        
                    HttpWebResponse response = null;

                    while (response == null)
                    {
                        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(resource);
                        request.Method = "GET";
                        request.Credentials = cache;
                        request.CookieContainer = myContainer;
                        request.PreAuthenticate = false;
                        request.AllowAutoRedirect = true;
                        request.Timeout = 10000;
                                                
                        response = null;

                        try
                        {
                            response = (HttpWebResponse)request.GetResponse();
                        }
                        catch
                        {                       
                        }
                    }

                    if (response != null)
                    {
                        try
                        {
                            bool allGood = merraList.SaveMERRA2DataFile(response, thisDate);
                            if (allGood == false)
                            {
                                MessageBox.Show("Downloaded file does not contain the expected dataset.  Check your Earthdata credentials at https://urs.earthdata.nasa.gov/.  Aborting download");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message);
                            return;
                        }
                    }
                    else if (response == null)
                    {
                        e.Result = thisInst;
                        return;
                    }

                }

                count++;
            });

            DoWorkDone = true;
            e.Result = thisInst;
        }
               
        private void BackgroundWorker_MERRADownload_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Updates the MERRA2 download progress bar
            BringToFront();
            string textForLabel = e.UserState.ToString();
            progbar.Value = e.ProgressPercentage;
            Text = "Continuum 3";
            lblprogbar.Text = textForLabel;
            Refresh();
        }

        private void BackgroundWorker_MERRADownload_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Continuum thisInst = (Continuum)e.Result;
            if (thisInst != null)
                if (thisInst.merraList != null)
                    thisInst.merraList.SetMERRA2LatLong(thisInst);
            Close();
        }
    }
}
