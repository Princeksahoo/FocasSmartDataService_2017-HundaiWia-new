using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Threading;
using FocasLib;
using FocasLibrary;
using System.Linq;
using DTO;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections;
using System.Text.RegularExpressions;
using MachineConnectLicenseDTO;

namespace FocasSmartDataCollection
{
    public class CreateClient
    {
        private string ipAddress;
        private ushort portNo;
        private string machineId;
        private string interfaceId;
        private string MName; 
      
        public string MachineName
        {
            get { return machineId; }
        }
        MachineSetting setting = default(MachineSetting);
        MachineInfoDTO machineDTO = default(MachineInfoDTO);
        private static string appPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        CncMachineType _cncMachineType = CncMachineType.cncUnknown;
        string _cncSeries = string.Empty;

        //LIC details
        bool _isCNCIdReadSuccessfully = false;//**********************************TODO - change to false before check-in
        private bool _isLicenseValid = false;       
        private int _timeDelayMainThread = 0;
       
        private static DateTime _serviceStartedTimeStamp = DateTime.Now;
        private static DateTime _nextLicCheckedTimeStamp = _serviceStartedTimeStamp;
             

        private Timer _timerAlarmHistory = null;      
        private Timer _timerAlarmsDataHundaiWia = null;

        object _lockerAlarmHistory = new object();       
        object _lockerAlarmsDataHundaiWia = new object();

        
        //static volatile object _lockerReleaseMemory = new object();

        //List<ushort> _focasHandles = new List<ushort>();
        bool _isOEMVersion = false;
        List<OffsetHistoryDTO> offsetHistoryList = new List<OffsetHistoryDTO>();
        List<LiveAlarm> _liveAlarmsGlobal = new List<LiveAlarm>();
        List<int> offsetHistoryRange = new List<int>();
        List<LiveAlarm> liveAlarmsLocal = new List<LiveAlarm>();
        BitArray _alarmsBits_R_Previous = null;
        BitArray _alarmsBits_D_Previous = null;
        private List<AlarmMaster_HundaiWia> _alarmMaster_HundaiWia = null;

        int _DataNotAvaliableCount = 0;
        short _alarmsNo_D_Previous = short.MinValue;

        public CreateClient(MachineInfoDTO machine)
        {            
            this.ipAddress = machine.IpAddress;
            this.portNo = (ushort)machine.PortNo;
            this.machineId = machine.MachineId;
            this.MName = this.machineId;
            this.interfaceId = machine.InterfaceId;
            this.setting = machine.Settings;
            this.machineDTO = machine;           
                       
            _timeDelayMainThread = (int)TimeSpan.FromSeconds(10).TotalMilliseconds;
            if (_timeDelayMainThread <= 4000) _timeDelayMainThread = 4000;

            int alaramsHistoryTimerDelay = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
            if (alaramsHistoryTimerDelay > 0 && alaramsHistoryTimerDelay < (int)TimeSpan.FromMinutes(1).TotalMilliseconds)
                alaramsHistoryTimerDelay = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
           
            int alarmsDataHundaiWiaTimerDelay = 0;
            int.TryParse(ConfigurationManager.AppSettings["TimeDelayAlarmsDataHundaiWiaThread"], out alarmsDataHundaiWiaTimerDelay);
            if (alarmsDataHundaiWiaTimerDelay > 0)
            {             
                alarmsDataHundaiWiaTimerDelay = (int)TimeSpan.FromSeconds(alarmsDataHundaiWiaTimerDelay).TotalMilliseconds;
            }          

            if (alaramsHistoryTimerDelay > 0)
                _timerAlarmHistory = new Timer(GetAlarmsData, null, 1000, alaramsHistoryTimerDelay);
            
            if (alarmsDataHundaiWiaTimerDelay > 0)
                _timerAlarmsDataHundaiWia = new Timer(GetAlarmsDataHundaiWia, null, 2000, alarmsDataHundaiWiaTimerDelay);

        }

        public void GetClient()
        {
            DateTime lastStatusTime = DateTime.Now;
            string prevStatus = "OK";
            while (true)
            {               
                try
                {
                    #region stop_service                   
                    if (ServiceStop.stop_service == 1)
                    {
                        try
                        {
                            Logger.WriteDebugLog("stopping the service. coming out of main while loop.");
                            break;
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteErrorLog(ex.Message);
                            break;
                        }
                    }
                    #endregion

                    try
                    {
                        if (Utility.CheckPingStatus(this.ipAddress))
                        {
                            #region LicenseCheck
                            if (!_isCNCIdReadSuccessfully)
                            {
                                string cncId = string.Empty;
                                List<string> cncIdList = FocasSmartDataService.licInfo.CNCData.Where(s => s.CNCdata1 != null).Select(s => s.CNCdata1).ToList();
                                _isLicenseValid = this.ValidateCNCSerialNo(this.machineId, this.ipAddress, this.portNo, cncIdList, out _isCNCIdReadSuccessfully, out cncId);

                                if (!_isLicenseValid)
                                {
                                    if (_isCNCIdReadSuccessfully)
                                    {
                                        Logger.WriteErrorLog("Lic Validation failed. Please contact AMIT/MMT.");
                                        break;
                                    }
                                    Thread.Sleep(TimeSpan.FromSeconds(10.0));
                                    continue;
                                }
                                //update table 
                                if (_isLicenseValid)
                                {                                   
                                    this.SetCNCDateTime(this.machineId, this.ipAddress, this.portNo);
                                }
                            }
                            #endregion
                            
                            ushort focasLibHandle = ushort.MinValue;
                            short ret = FocasData.cnc_allclibhndl3(ipAddress, portNo, 30, out focasLibHandle);
                            FocasData.cnc_freelibhndl(focasLibHandle);
                            if (ret == 0)
                            {
                                if ((DateTime.Now - lastStatusTime).TotalMinutes > 1 || prevStatus != "OK")
                                {
                                    DatabaseAccess.UpdateConnectionStatus(this.machineId, this.machineDTO.ControlerType, "OK", DateTime.Now);
                                    lastStatusTime = DateTime.Now;
                                    prevStatus = "OK";
                                }
                            }
                            else
                            {
                                if ((DateTime.Now - lastStatusTime).TotalMinutes > 1 || prevStatus != "NOT OK")
                                {
                                    DatabaseAccess.UpdateConnectionStatus(this.machineId, this.machineDTO.ControlerType, "NOT OK", DateTime.Now);
                                    lastStatusTime = DateTime.Now;
                                    prevStatus = "NOT OK";
                                }
                            }
                           
                        }
                        else
                        {
                            //Log the data to MachineConnectionStatus_HyundaiWia
                            if ((DateTime.Now - lastStatusTime).TotalMinutes > 10 || prevStatus != "Ping Failed")
                            {
                                DatabaseAccess.UpdateConnectionStatus(this.machineId, this.machineDTO.ControlerType, "Ping Failed", DateTime.Now);
                                lastStatusTime = DateTime.Now;
                                prevStatus = "Ping Failed";
                            }

                            if (ServiceStop.stop_service == 1) break;
                            Thread.Sleep(1000 * 4);
                        }
                        
                        if (_timeDelayMainThread > 0)
                        {
                            if (ServiceStop.stop_service == 1)  break;
                            Thread.Sleep(_timeDelayMainThread);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.WriteErrorLog("Exception inside main while loop : " + e.ToString());                      
                        Thread.Sleep(1000 * 4);                       
                    }
                    finally
                    {   

                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteErrorLog("Exception from main while loop : " + ex.ToString());
                    Thread.Sleep(2000);
                }
            }
            this.CloseTimer();
            Logger.WriteDebugLog("End of while loop." + Environment.NewLine + "------------------------------------------");
        }

        private void SetCNCDateTime(string machineId, string ipAddress, ushort port)
        {           
            Ping ping = null;
            ushort focasLibHandle = 0;
            try
            {
                ping = new Ping();
                PingReply pingReply = null;
                int count = 0;
                while (true)
                {
                    pingReply = ping.Send(ipAddress, 10000);
                    if (pingReply.Status != IPStatus.Success)
                    {
                        if (ServiceStop.stop_service == 1) break;
                        Logger.WriteErrorLog("Not able to ping. Ping status = " + pingReply.Status.ToString());
                        Thread.Sleep(2000);
                    }
                    else if (pingReply.Status == IPStatus.Success || ServiceStop.stop_service == 1 || count == 4)
                    {
                        break;
                    }
                    ++count;
                }
                if (pingReply.Status == IPStatus.Success)
                {
                    int num2 = FocasData.cnc_allclibhndl3(ipAddress, port, 10, out focasLibHandle);
                    if (num2 == 0)
                    {
                        FocasData.SetCNCDate(focasLibHandle, DateTime.Now);
                        FocasData.SetCNCTime(focasLibHandle, DateTime.Now);                                             
                    }
                    else
                    {
                        Logger.WriteErrorLog("Not able to connect to machine. cnc_allclibhndl3 status = " + num2.ToString());
                    }
                }
                else
                {
                    Logger.WriteErrorLog("Not able to ping. Ping status = " + pingReply.Status.ToString());
                }
            }
            catch (Exception ex)
            {
                Logger.WriteDebugLog(ex.ToString());
            }
            finally
            {
                if (ping != null)
                {
                    ping.Dispose();
                }
                if (focasLibHandle != 0)
                {
                    short num3 = FocasData.cnc_freelibhndl(focasLibHandle);                   
                }
            }            
        }
              
        private bool ValidateCNCSerialNo(string machineId, string ipAddress, ushort port, List<string> cncSerialnumbers, out bool isLicCheckedSucessfully, out string cncID)
        {
            bool result = false;
            isLicCheckedSucessfully = true;
            Ping ping = null;
            ushort focasLibHandle = 0;
            cncID = string.Empty;

            try
            {
                ping = new Ping();
                PingReply pingReply = null;
                while (true)
                {
                    pingReply = ping.Send(ipAddress, 10000);
                    if (pingReply.Status != IPStatus.Success)
                    {
                        if (ServiceStop.stop_service == 1) break;
                        Logger.WriteErrorLog("Not able to ping. Ping status = " + pingReply.Status.ToString());
                        Thread.Sleep(10000);
                    }
                    else if (pingReply.Status == IPStatus.Success || ServiceStop.stop_service == 1)
                    {
                        break;
                    }
                }
                if (pingReply.Status == IPStatus.Success)
                {
                    int num2 = FocasData.cnc_allclibhndl3(ipAddress, port, 10, out focasLibHandle);
                    if (num2 == 0)
                    {
                        string text = FocasData.ReadCNCId(focasLibHandle);
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (cncSerialnumbers.Contains(text))
                            {
                                cncID = text;
                                result = true;
                            }
                        }
                        else
                        {
                            isLicCheckedSucessfully = false;
                        }
                    }
                    else
                    {
                        Logger.WriteErrorLog("Not able to connect to machine. cnc_allclibhndl3 status = " + num2.ToString());
                        isLicCheckedSucessfully = false;
                    }
                }
                else
                {
                    Logger.WriteErrorLog("Not able to ping. Ping status = " + pingReply.Status.ToString());
                    isLicCheckedSucessfully = false;
                }
            }
            catch (Exception ex)
            {
                isLicCheckedSucessfully = false;
                Logger.WriteDebugLog(ex.ToString());
            }
            finally
            {
                if (ping != null)
                {
                    ping.Dispose();
                }
                if (focasLibHandle != 0)
                {
                    short num3 = FocasData.cnc_freelibhndl(focasLibHandle);
                    //if (num3 != 0) _focasHandles.Add(focasLibHandle);
                }
            }
            return result;
        }

        private bool ValidateMachineModel(string machineId, string ipAddress, ushort port)
        {
            bool result = false;           
            Ping ping = null;
            ushort focasLibHandle = 0;
            try
            {
                ping = new Ping();
                PingReply pingReply = null;
                while (true)
                {
                    pingReply = ping.Send(ipAddress, 10000);
                    if (pingReply.Status != IPStatus.Success)
                    {
                        if (ServiceStop.stop_service == 1) break;
                        Logger.WriteErrorLog("Not able to ping. Ping status = " + pingReply.Status.ToString());
                        Thread.Sleep(10000);
                    }
                    else if (pingReply.Status == IPStatus.Success || ServiceStop.stop_service == 1)
                    {
                        break;
                    }
                }
                if (pingReply.Status == IPStatus.Success)
                {
                    int num2 = FocasData.cnc_allclibhndl3(ipAddress, port, 10, out focasLibHandle);
                    if (num2 == 0)
                    {
                        int mcModel = FocasData.ReadParameterInt(focasLibHandle, 4133);
                        int maxSpeedOnMotor = FocasData.ReadParameterInt(focasLibHandle, 4020);
                        int maxSpeedOnSpindle = FocasData.ReadParameterInt(focasLibHandle, 3741);
                        if (mcModel > 0)
                        {
                            DatabaseAccess.UpdateMachineModel(machineId, mcModel);
                        }
                    }
                    else
                    {
                        Logger.WriteErrorLog("Not able to connect to machine. cnc_allclibhndl3 status = " + num2.ToString());                       
                    }
                }
                else
                {
                    Logger.WriteErrorLog("Not able to ping. Ping status = " + pingReply.Status.ToString());                   
                }
            }
            catch (Exception ex)
            {              
                Logger.WriteDebugLog(ex.ToString());
            }
            finally
            {
                if (ping != null)
                {
                    ping.Dispose();
                }
                if (focasLibHandle != 0)
                {
                    short num3 = FocasData.cnc_freelibhndl(focasLibHandle);
                   // if (num3 != 0) _focasHandles.Add(focasLibHandle);
                }
            }
            return result;
        }
       
        public static string SafePathName(string name)
        {
            StringBuilder str = new StringBuilder( name);           

            foreach (char c in System.IO.Path.GetInvalidPathChars())
            {
                str = str.Replace(c, '_');
            }
            return str.ToString();
        }

        public static bool CreateDirectory(string masterProgramFolderPath)
        {
            var safeMasterProgramFolderPath = SafePathName(masterProgramFolderPath);
             if (!Directory.Exists(safeMasterProgramFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(safeMasterProgramFolderPath);
                }
                catch(Exception ex)
                {
                    Logger.WriteErrorLog(ex.ToString());
                    return false;
                }
            }
            return true;
        }      

        public void GetAlarmsData(Object stateObject)
        {
            if (!_isLicenseValid) return;
            if (Monitor.TryEnter(_lockerAlarmHistory, 100))
            {               
                try
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
                    Thread.CurrentThread.Name = "AlarmsHistory-" + Utility.SafeFileName(this.machineId);

                    if (Utility.CheckPingStatus(this.ipAddress))
                    {
                        CheckMachineType();
                        Logger.WriteDebugLog("Reading Alarms History data for control type." + _cncMachineType.ToString());
                        if (_cncMachineType == CncMachineType.cncUnknown) return;
                        DataTable dt = default(DataTable);
                        if (_cncMachineType == CncMachineType.Series300i ||
                            _cncMachineType == CncMachineType.Series310i ||
                            _cncMachineType == CncMachineType.Series320i ||
                             _cncMachineType == CncMachineType.Series350i ||
                            _cncMachineType == CncMachineType.Series0i)
                        {
                            dt = FocasData.ReadAlarmHistory(machineId, ipAddress, portNo);
                        }
                        else
                        {
                            //oimc,210i
                            dt = FocasData.ReadAlarmHistory18i(machineId, ipAddress, portNo);
                        }
                        //Logger.WriteDebugLog(string.Format("Deleting the records from AlarmTemp table for machine {0}", machineName));
                        DatabaseAccess.DeleteAlarmTempRecords(machineId);
                        DatabaseAccess.InsertAlarms(dt, machineId);
                        Logger.WriteDebugLog("Completed reading Alarms History data.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteDebugLog(ex.ToString());
                }
                finally
                {                   
                    Monitor.Exit(_lockerAlarmHistory);
                               
                }
            }

        }
              
        public void CloseTimer()
        {
            if (_timerAlarmHistory != null) _timerAlarmHistory.Dispose();
            if (_timerAlarmsDataHundaiWia != null) _timerAlarmsDataHundaiWia.Dispose();            
        }   

        public void CheckMachineType()
        {
            if (_cncSeries.Equals(string.Empty))
            {
                ushort focasLibHandle = ushort.MinValue;
                short ret = FocasData.cnc_allclibhndl3(ipAddress, portNo, 4, out focasLibHandle);
                if (ret == 0)
                {
                    if (FocasData.GetFanucMachineType(focasLibHandle, ref _cncMachineType, out _cncSeries) != 0)
                    {
                        Logger.WriteErrorLog("Failed to get system info. method failed cnc_sysinfo()");
                    }
                    Logger.WriteDebugLog("CNC control type  = " + _cncMachineType.ToString() + " , " + _cncSeries);
                }
                ret = FocasData.cnc_freelibhndl(focasLibHandle);
                //if (ret != 0) _focasHandles.Add(focasLibHandle);
            }
        }       

        public void GetAlarmsDataHundaiWia(Object stateObject)
        {          
            if (!_isLicenseValid) return;
            if (Monitor.TryEnter(_lockerAlarmsDataHundaiWia, 100))
            {
                try
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
                    Thread.CurrentThread.Name = "AlarmsData-HyundaiWia-" + Utility.SafeFileName(this.machineId);

                    if (_alarmMaster_HundaiWia == null)
                    {
                        _alarmMaster_HundaiWia = DatabaseAccess.GetAlarmsMaster_HundaiWia(this.machineId);
                        if (_alarmMaster_HundaiWia != null && _alarmMaster_HundaiWia.Count() == 0)
                            Logger.WriteDebugLog(string.Format("Alarm Master not found in table \"AlarmMaster_HyundaiWia\" for machine = {0}", this.machineId));
                    }
                  

                    if (Utility.CheckPingStatus(this.ipAddress))
                    {
                        ReadAlarmsDataHundaiWia(this.machineId, this.ipAddress, this.portNo);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteDebugLog(ex.ToString());
                }
                finally
                {
                    Monitor.Exit(_lockerAlarmsDataHundaiWia);
                }
            }
        }
       
        private void ReadAlarmsDataHundaiWia(string machineId, string ipAddress, ushort portNo)
        {
            int ret = 0;
            ushort focasLibHandle = 0;
            try
            {
                ret = FocasData.cnc_allclibhndl3(ipAddress, portNo, 10, out focasLibHandle);
                if (ret != 0)
                {
                    Logger.WriteErrorLog("ReadAlarmsDataHyundaiWia => cnc_allclibhndl3() failed. return value is = " + ret);
                    Thread.Sleep(1000);
                    return;
                }              

                if (this.setting.AlarmsHundaiLocations.Count == 0)
                {
                    Logger.WriteDebugLog(string.Format("Master for R & D location data not found in AlarmsParametersSettings_HyundaiWia table"));
                    return;
                }

                var CNCTimeStamp = FocasData.ReadCNCTimeStamp(focasLibHandle);

                foreach (var item in this.setting.AlarmsHundaiLocations)
                {
                    if (item.PrarameterType <= 0) continue;

                    if (item.PrarameterType == 1)
                    {
                        //Logger.WriteDebugLog("Reading Alarms data from R location. StartLocation = " + item.StartLocation + " ; EndLocation = " + item.EndLocation);

                        List<byte> values = FocasData.ReadPMCRangeByte(focasLibHandle, 5, (ushort)item.StartLocation, (ushort)item.EndLocation);
                        if (values == null) continue;

                        BitArray alarmsBits_current = new BitArray(values.Take(item.EndLocation - item.StartLocation + 1).ToArray());
                        //compare both BitArray, if revious low and now high, insert to database.
                        if (_alarmsBits_R_Previous == null)
                        {
                            _alarmsBits_R_Previous = new BitArray(alarmsBits_current);
                        }
                        int addr = item.StartLocation;
                        for (int i = 0; i < alarmsBits_current.Length; i++)
                        {
                            //Logger.WriteDebugLog(string.Format("{0} : Value = {1} ", string.Format("R{0}.{1}", (addr + i / 8), (i % 8)), alarmsBits_current[i].ToString()));
                            //compare prevous and current status of each bit
                            if (_alarmsBits_R_Previous[i] == false && alarmsBits_current[i] == true)
                            {
                                _DataNotAvaliableCount = 0;
                                string strLocation = string.Format("R{0}.{1}", (addr + i / 8), (i % 8));
                                string alarmDesc = _alarmMaster_HundaiWia.Where(s => s.RLocation.Equals(strLocation, StringComparison.OrdinalIgnoreCase)).Select(j => j.AlarmDesc).FirstOrDefault();
                                Logger.WriteDebugLog(string.Format("Alarm started : {0} \"{1}\"", strLocation, alarmDesc ?? " Alarm Desc not found in Table \"AlarmMaster_HundaiWia\""));
                                //get the description from database for R location, insert to Alarms_HundaiWia table
                                DatabaseAccess.InsertAlarmsForHundaiWia(this.machineId, strLocation, alarmDesc, CNCTimeStamp);
                            }
                        }
                        _alarmsBits_R_Previous = new BitArray(alarmsBits_current);
                    }
                    else if (item.PrarameterType == 2)
                    {
                        //Logger.WriteDebugLog("Reading Alarms data from D location. StartLocation = " + item.StartLocation + " ; EndLocation = " + item.EndLocation);
                        List<byte> values = FocasData.ReadPMCRangeByte(focasLibHandle, 9, (ushort)item.StartLocation, (ushort)item.EndLocation);
                        if (values == null) continue;
                        BitArray alarmsBits_D_current = new BitArray(values.Take(item.EndLocation - item.StartLocation + 1).ToArray());
                        if (_alarmsBits_D_Previous == null)
                        {
                            _alarmsBits_D_Previous = new BitArray(alarmsBits_D_current);
                        }
                        //compare both BitArray, if revious low and now high, insert to database.
                        int addr = item.StartLocation;
                        for (int i = 0; i < alarmsBits_D_current.Length; i++)
                        {
                            //compare prevous and current status of each bit
                            if (_alarmsBits_D_Previous[i] == false && alarmsBits_D_current[i] == true)
                            {
                                _DataNotAvaliableCount = 0;
                                string strLocation = string.Format("D{0}.{1}", addr + i / 8, i % 8);
                                string alarmDesc = _alarmMaster_HundaiWia.Where(s => s.RLocation.Equals(strLocation, StringComparison.OrdinalIgnoreCase)).Select(j => j.AlarmDesc).FirstOrDefault();
                                Logger.WriteDebugLog(string.Format("Alarm started : {0} \"{1}\"", strLocation, alarmDesc ?? " Alarm Desc not found in Table \"AlarmMaster_HundaiWia\""));
                                //get the description from database for R location, insert to Alarms_HundaiWia table
                                DatabaseAccess.InsertAlarmsForHundaiWia(this.machineId, strLocation, alarmDesc, CNCTimeStamp);
                            }
                        }
                        _alarmsBits_D_Previous = new BitArray(alarmsBits_D_current);
                    }
                    else if (item.PrarameterType == 3)
                    {
                        //Logger.WriteDebugLog("Reading Alarms data from D location. StartLocation = " + item.StartLocation + " ; EndLocation = " + item.EndLocation);
                        short value = FocasData.ReadPMCOneWord(focasLibHandle, 9, (ushort)item.StartLocation, (ushort)(item.EndLocation + 1));
                        if (value == short.MinValue) continue;

                        if (_alarmsNo_D_Previous == short.MinValue)
                        {
                            _alarmsNo_D_Previous = value;
                        }
                        //compare both D Value, if state changes, insert to database.
                        //compare prevous and current status
                        if (_alarmsNo_D_Previous != value && value > 0)
                        {
                            _DataNotAvaliableCount = 0;
                            string strLocation = string.Format("D{0}", item.StartLocation);
                            string alarmDesc = _alarmMaster_HundaiWia.Where(s => s.RLocation.Equals(value.ToString(), StringComparison.OrdinalIgnoreCase)).Select(j => j.AlarmDesc).FirstOrDefault();
                            Logger.WriteDebugLog(string.Format("Alarm started : {0}-{1}\"{2}\"", strLocation, value.ToString(), alarmDesc ?? " Alarm Desc not found in Table \"AlarmMaster_HundaiWia\""));
                            //get the description from database for R location, insert to Alarms_HundaiWia table
                            DatabaseAccess.InsertAlarmsForHundaiWia(this.machineId, value.ToString(), alarmDesc, CNCTimeStamp);
                        }

                        _alarmsNo_D_Previous = value;
                    }                    
                }

                if (_DataNotAvaliableCount > 0 && _DataNotAvaliableCount % 300 == 0) //TODO 60 to 120
                {
                    Logger.WriteDebugLog("No Alarms from CNC since sec = " + _DataNotAvaliableCount);                   
                }
                if (_DataNotAvaliableCount >= int.MaxValue) _DataNotAvaliableCount = 0;
                _DataNotAvaliableCount++;

            }
            catch (Exception exx)
            {
                Logger.WriteErrorLog(exx.ToString());
            }
            finally
            {
                if (focasLibHandle > 0)
                {
                    var r = FocasData.cnc_freelibhndl(focasLibHandle);                   
                }
            }
        }
       
    }
}

