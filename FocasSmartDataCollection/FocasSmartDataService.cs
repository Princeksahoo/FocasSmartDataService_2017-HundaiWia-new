using System.Configuration;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System;
using System.Data.SqlClient;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Net.Cache;
using System.Diagnostics;
using MachineConnectLicenseDTO;
using System.Data;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
namespace FocasSmartDataCollection
{
    public partial class FocasSmartDataService : ServiceBase
    {
        List<Thread> threads = new List<Thread>();
        List<CreateClient> clients = new List<CreateClient>();       
        private readonly static object padlockDatabaseBackup = new object();
        private readonly static object padlockCleanupProcess = new object();
        string appPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public static LicenseTerms licInfo = new LicenseTerms();
        private volatile bool stopping = false;
        TimeSpan time;

        public FocasSmartDataService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
            Thread.CurrentThread.Name = "MachineConnect_Service";

            if (!Directory.Exists(appPath + "\\Logs\\"))
            {
                Directory.CreateDirectory(appPath + "\\Logs\\");
            }            

            if (!Directory.Exists(appPath + "\\Licensing\\"))
            {
                Directory.CreateDirectory(appPath + "\\Licensing\\");
            }

            try
            {
                licInfo.CNCData = new List<CNCData>();
                var licFolderPath = Path.Combine(appPath, "Licensing");
                var files = Directory.GetFiles(licFolderPath, "*.lic", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    LicenseTerms licDto = null;
                    Utility.validateLicenseFile(file, ref licDto);
                    if (licDto == null || licDto.CNCData == null || licDto.CNCData.Count == 0)
                    {
                        Logger.WriteErrorLog("Invalid Lic file");
                        return;
                    }
                    else
                    {
                        licInfo.ComputerSerialNo = licDto.ComputerSerialNo;
                        licInfo.Customer = licDto.Customer;
                        licInfo.Email = licDto.Email;
                        licInfo.ExpiresAt = licDto.ExpiresAt;
                        licInfo.LicType = licDto.LicType;
                        licInfo.Plant = licDto.Plant;
                        licInfo.StartDate = licDto.StartDate;
                        foreach (var cncdata in licDto.CNCData)
                        {
                            if (licInfo.CNCData.Exists(data => data.CNCdata1 == cncdata.CNCdata1 && data.IsOEM == cncdata.IsOEM) == false)
                            {
                                licInfo.CNCData.Add(cncdata);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is System.FormatException)
                {
                    Logger.WriteErrorLog("Invald lic file....or file has been tempared..");
                }
                else
                    Logger.WriteErrorLog(ex.Message);
                return;
            }

            if (licInfo == null || licInfo.CNCData == null || licInfo.CNCData.Count == 0)
            {
                Logger.WriteErrorLog("Invalid Lic file");
                return;
            }

            ServiceStop.stop_service = 0;
            List<MachineInfoDTO> machines = DatabaseAccess.GetTPMTrakMachine();
            if (machines.Count == 0)
            {
                Logger.WriteDebugLog("No machine is enabled for DNCTransferEnabled. modify the machine setting and restart the service.");
                return;
            }

            try
            {
                foreach (MachineInfoDTO machine in machines)
                {
                    CreateClient client = new CreateClient(machine);
                    clients.Add(client);

                    ThreadStart job = new ThreadStart(client.GetClient);
                    Thread thread = new Thread(job);
                    thread.Name = Utility.SafeFileName(machine.MachineId);
                    thread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
                    thread.Start();
                    threads.Add(thread);
                    Logger.WriteDebugLog(string.Format("Machine {0} started for DataCollection with IP = {1} , PORT = {2}.", machine.MachineId, machine.IpAddress, machine.PortNo));
                }
            }
            catch (Exception e)
            {
                Logger.WriteErrorLog(e.Message);
            }


            //Pramod
            try
            {
                ThreadStart cleanupProcess = new ThreadStart(cleanupProcessService);
                Thread thread = new Thread(cleanupProcess);
                thread.Name = "cleanupProcess";
                thread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
                thread.Start();
                threads.Add(thread);
            }
            catch (Exception ex)
            {
                Logger.WriteErrorLog(ex.ToString());
            }           
            
            ////Pramod
            //try
            //{
            //    ThreadStart threadDataBaseBackUpService = new ThreadStart(dataBaseBackUpService);
            //    Thread thread = new Thread(threadDataBaseBackUpService);
            //    thread.Name = "DataBaseBackUp";
            //    thread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            //    thread.Start();
            //    threads.Add(thread);
            //}
            //catch (Exception ex)
            //{
            //    Logger.WriteErrorLog(ex.ToString());
            //}

        }

        protected override void OnStop()
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            if (string.IsNullOrEmpty(Thread.CurrentThread.Name))
            {
                Thread.CurrentThread.Name = "MachineConnect_Service";
            }

            stopping = true;
            ServiceStop.stop_service = 1;

           //lock (padlockDatabaseBackup)
           // {
           //     Monitor.Pulse(padlockDatabaseBackup);
           // }

            lock (padlockCleanupProcess)
            {
                Monitor.Pulse(padlockCleanupProcess);
            }
           
            Thread.SpinWait(60000 * 10);
            try
            {
                Logger.WriteDebugLog("Service Stop request has come!!! ");
                Logger.WriteDebugLog("Thread count is: " + threads.Count.ToString());
                foreach (Thread thread in threads)
                {
                    Logger.WriteDebugLog("Stopping the machine - " + thread.Name);
                    if (thread != null && thread.IsAlive)
                    {
                        // Try to stop by allowing the thread to stop on its own.
                        this.RequestAdditionalTime(6000);
                        if (!thread.Join(6000))
                        {
                            thread.Abort();
                            Logger.WriteDebugLog("Aborted.");
                        }
                    }
                }
                threads.Clear();
            }
            catch (Exception ex)
            {
                Logger.WriteErrorLog(ex.ToString());
            }
            finally
            {
                foreach (CreateClient client in clients)
                {
                    client.CloseTimer();
                }
                clients.Clear();
            }
            Logger.WriteDebugLog("Service has stopped.");
        }

        internal void StartDebug()
        {
            OnStart(null);
        }

        void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            Exception e = args.ExceptionObject as Exception;
            if (e != null)
            {
                Logger.WriteErrorLog("Unhandled Exception caught : " + e.ToString());
                Logger.WriteErrorLog("Runtime terminating:" + args.IsTerminating);
                var threadName = Thread.CurrentThread.Name;
                Logger.WriteErrorLog("Exception from Thread = " + threadName);
                Process p = Process.GetCurrentProcess();
                StringBuilder str = new StringBuilder();
                if (p != null)
                {
                    str.AppendLine("Total Handle count = " + p.HandleCount);
                    str.AppendLine("Total Threads count = " + p.Threads.Count);
                    str.AppendLine("Total Physical memory usage: " + p.WorkingSet64);

                    str.AppendLine("Peak physical memory usage of the process: " + p.PeakWorkingSet64);
                    str.AppendLine("Peak paged memory usage of the process: " + p.PeakPagedMemorySize64);
                    str.AppendLine("Peak virtual memory usage of the process: " + p.PeakVirtualMemorySize64);
                    Logger.WriteErrorLog(str.ToString());
                }
                Thread.CurrentThread.Abort();
                //while (true)
                //    Thread.Sleep(TimeSpan.FromHours(1));

            }
        }

        private void dataBaseBackUpService()
        {           
            Logger.WriteDebugLog("Database maintenance thread started...");
            bool isfirstTimeBackup;
            BackupDB.DBBackUpDate(out isfirstTimeBackup, true);

            while (!stopping)
            {
                try
                {
                    BackupDB bckdb = new BackupDB();
                    bckdb.fun_caller();                   
                }
                catch (Exception ex)
                {
                    Logger.WriteErrorLog("Exception : " + ex.Message);
                }
                finally
                {
                    if (!stopping)
                    {
                        lock (padlockDatabaseBackup)
                        {
                            Monitor.Wait(padlockDatabaseBackup, TimeSpan.FromMinutes(60));
                            //Logger.WriteDebugLog("Database thread awake...");
                        }
                    }
                }
            }
        }

        static DateTime CDT = DateTime.Now.AddDays(1);
        private void cleanupProcessService()
        {
            Logger.WriteDebugLog("cleanupProcessService maintenance thread started...");          
            while (!stopping)
            {

                try
                {
                    if (CDT < DateTime.Now)
                    {
                        CDT = CDT.AddDays(10);
                        Logger.WriteDebugLog("clean up process started.");
                        CleanUpProcess.DeleteFiles("Logs", "");                       
                        {
                            //TODO - Change the query.... 
                            //DatabaseAccess.DeleteTableData(60, "Alarms_HyundaiWia");                           
                        }                        
                        Logger.WriteDebugLog("clean up process Ended.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteErrorLog(ex.ToString());
                }
                finally
                {
                    if (!stopping)
                    {
                        lock (padlockCleanupProcess)
                        {
                            Monitor.Wait(padlockCleanupProcess, TimeSpan.FromDays(1));
                            //Logger.WriteDebugLog("clean up process thread awake...");
                        }
                    }
                }
            }
        }     
    }

}







