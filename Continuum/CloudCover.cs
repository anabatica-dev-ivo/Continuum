﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static ContinuumNS.Reference;
using static ContinuumNS.ReferenceCollection;
using System.Windows.Forms;

namespace ContinuumNS
{
    public class CloudCover
    {
        public Reference.XYIndices coords;
        public MERRA2_CloudData[] merraCloud;

        public struct MERRA2_CloudData
        {
            public DateTime thisDate;
            public double isccpCloudCover;
            public double modisCloudCover;
            public double modisCloudThickness;
        }

        

        public bool GetPullXYIndices(string folderLocation)
        {           
                        
            string[] refDataFiles = Directory.GetFiles(folderLocation, "*.ascii");
            string line;

            if (refDataFiles == null)
            {
                MessageBox.Show("Check specified folderpath and try again.");
                return false;
            }

            StreamReader file;

            try
            {
                file = new StreamReader(refDataFiles[0]);
            }
            catch
            {
                MessageBox.Show("Error opening MERRA data file. Check that it's not open in another program.");
                return false;
            }

            int numLats = 0;
            int numLongs = 0;
            double[] lats = new double[0];
            double[] longs = new double[0];

            while ((line = file.ReadLine()) != null)
            {
                string[] substrings = line.Split(',');

                if (substrings[0] == "lat") // read in all latitudes
                {
                    numLats = substrings.Length - 1;
                    Array.Resize(ref lats, numLats);

                    for (int i = 0; i < numLats; i++)
                        lats[i] = Convert.ToDouble(substrings[i + 1]);
                }

                if (substrings[0] == "lon") // read in all longitudes
                {
                    numLongs = substrings.Length - 1;
                    Array.Resize(ref longs, numLongs);

                    for (int i = 0; i < numLongs; i++)
                        longs[i] = Convert.ToDouble(substrings[i + 1]);
                }
            }

            file.Close();                        

            // Go through nodesToPull and assign Xind and Yind
            
            bool gotLat = false;
            bool gotLong = false;

            for (int latInd = 0; latInd < numLats; latInd++)
            {
                if (lats[latInd] == coords.Lat)
                {                
                    coords.X_ind = latInd;
                    gotLat = true;
                }
            }

            for (int longInd = 0; longInd < numLongs; longInd++)
            {
                if (longs[longInd] == coords.Lon)
                {                        
                    coords.Y_ind = longInd;
                    gotLong = true;
                }
            }

            if (gotLat == false || gotLong == false)
            {
                MessageBox.Show("The reference data file does not contain specified lat/long node. Lat: " + coords.Lat.ToString() + ", Long: " + coords.Lon.ToString());
                return false;
            }
            
            return true;
                
        }
    }
}
