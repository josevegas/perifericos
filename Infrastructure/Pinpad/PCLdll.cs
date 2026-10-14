using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace PuntoVenta.Infrastructure.Pinpad
{
    /// <summary>
    /// Funciones de la libreria PCL de Ingenico
    /// </summary>
    class PCLdll
    {
        /// <summary>
        /// tamaño maximo del campo puerto
        /// </summary>
        private const int PORT_SIZE = 7;
        /// <summary>
        /// Tamaño maximo del nombre de la terminal que se recibe 
        /// </summary>
        private const int MAX_NAME_SIZE = 248;
        /// <summary>
        /// Tamaño maximo de la direccion recibida para las terminales bluethooth
        /// </summary>
        private const int ADDRESS_SIZE = 13;
        /// <summary>
        /// Max ip address size
        /// </summary>
        private const int ADDRESS_IP_SIZE = 20;

        /// <summary>
        /// Initiate the connection between the Windows device and the companion.
        /// </summary>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool startPclService();

        /// <summary>
        /// Close the connection between the Windows device and the companion.
        /// </summary>
        /// <returns>TRUE in case of success, FALSE in case of failure</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool stopPclService();

        /// <summary>
        /// Get PPP server status
        /// </summary>
        /// <param name="result">status of the operation (char)1 == OK</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool serverStatus(StringBuilder result);

        /// <summary>
        /// Launch Telium update
        /// </summary>
        /// <param name="result">status of the operation,  0 == ok</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool doUpdate(ref UInt32 result);

        /// <summary>
        /// Get battery level
        /// </summary>
        /// <param name="Level">Pointer on int to receive battery level. Battery level is 0 if the terminal is not portable or has no battery.</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool getBatteryLevel(ref UInt32 Level);

        /// <summary>
        /// This structure is use by function getTerminalInfo to return serial and product number.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class spmInfo_t
        {
            /// <summary>
            /// Serial number of the remote device. 
            /// </summary>
            public UInt32 dwSerialNumber;
            /// <summary>
            /// Product number of the remote devic
            /// </summary>
            public UInt32 dwProductNumber;
        }

        /// <summary>
        /// Get Telium terminal information
        /// </summary>
        /// <param name="spmInfo">information structure</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool getTerminalInfo(spmInfo_t spmInfo);

        /// <summary>
        /// This structure is use by function tmsReadParam to return list of ssl profiles.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class tms_ssl_parameter_t
        {
            /// <summary>
            /// number of profiles 
            /// </summary>
            public int nb_ssl_profile;
            /// <summary>
            /// 	matrix of profiles 
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20 * 12)]
            public string ssl_profiles;
            /// <summary>
            /// the current profile 
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
            public string current_ssl_profile;
        }

        /// <summary>
        /// Read TMS parameters of terminal.
        /// </summary>
        /// <param name="addr">	Buffer to receive ip address or domain name (max size 258).</param>
        /// <param name="addrSize">The size of input buffer addr.</param>
        /// <param name="port">Buffer to receive port (max size 6).</param>
        /// <param name="portSize">The size of input buffer port.</param>
        /// <param name="identifier">Buffer to receive identifier (max size 11).</param>
        /// <param name="identifierSize">The size of input buffer identifier.</param>
        /// <param name="ssl_profiles">Structure to receive SSL information.</param>
        /// <param name="result">Status of the operation (0=OK, 1=KO)</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern bool tmsReadParam(StringBuilder addr, uint addrSize, StringBuilder port, uint portSize,
                                                StringBuilder identifier, uint identifierSize, [In, Out] tms_ssl_parameter_t ssl_profiles,
                                                StringBuilder result);

        /// <summary>
        /// Configure TMS parameters of terminal.
        /// </summary>
        /// <param name="addr">TMS IP address or domain name. Empty string or NULL to keep the current parameter.</param>
        /// <param name="port">TMS port. Empty string or NULL to keep the current parameter.</param>
        /// <param name="identifier">TMS Identifier. Empty string or NULL to keep the current parameter.</param>
        /// <param name="ssl_profile">The name of TMS SSL profile. If NULL to keep the current parameter and if empty string to disable SSL.</param>
        /// <param name="result">Status of the operation (0=OK, 1=KO)</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern bool tmsWriteParam(string addr, string port, string identifier, string ssl_profile, string result);

        /// <summary>
        /// Get Telium terminal full serial number
        /// </summary>
        /// <param name="fullSN">buffer to receive full serial number</param>
        /// <param name="fullSNSize">buffer size</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern bool getFullSerialNumber(StringBuilder fullSN, UInt32 fullSNSize);

        /// <summary>
        /// The USB_COMPANION_INFO structure provides information about a USB companion.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct USB_COMPANION_INFO
        {
            /// <summary>
            /// COM port used by the companion
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = PORT_SIZE)]
            public string szPort;
            /// <summary>
            /// Specifies whether the companion is activated.
            /// </summary>
            public bool fActivated;
            /// <summary>
            /// Name of the companion (terminal type-serial number, eg. "iCT250-12345678")
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_NAME_SIZE)]
            public string szName;             //COM
        }

        /// <summary>
        /// Pointer The USB_COMPANION_INFO structure provides information about a USB companion.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PUSB_COMPANION_INFO
        {
            /// <summary>
            /// COM port used by the companion
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
            public USB_COMPANION_INFO[] USB;
        }

        /// <summary>
        /// The BLUETOOTH_COMPANION_INFO structure provides information about a Bluetooth companion.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct BLUETOOTH_COMPANION_INFO
        {
            /// <summary>
            /// Bluetooth address of the companion, eg. "547F54ABCDEF"
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADDRESS_SIZE)]
            public string Address;
            /// <summary>
            /// Specifies whether the companion is activated.
            /// </summary>
            public bool fActivated;
            /// <summary>
            /// Bluetooth name of the companion
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_NAME_SIZE)]
            public string szName;
        }

        /// <summary>
        /// Pointer The BLUETOOTH_COMPANION_INFO structure provides information about a Bluetooth companion.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PBLUETOOTH_COMPANION_INFO
        {
            /// <summary>
            /// Bluetooth address of the companion, eg. "547F54ABCDEF"
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
            public BLUETOOTH_COMPANION_INFO[] BT;
        }

        /// <summary>
        /// Retrieves the list of IP Ingenico companions on the same network of the Windows device.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct IP_COMPANION_INFO
        {
            /// <summary>
            /// IP adresse used by the terminal
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADDRESS_IP_SIZE)]
            public string AdressIP;
            /// <summary>
            /// MAC adresse of the terminal
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADDRESS_IP_SIZE)]
            public string AdressMAC;
            /// <summary>
            /// Specifies whether the companion is activated.
            /// </summary>
            public bool fActivated;
            /// <summary>
            /// Name of the companion (terminal type-serial number, eg. "iCT250-12345678")
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_NAME_SIZE)]
            public string szName;             //COM
            /// <summary>
            /// Boolean to use ssl connection
            /// </summary>
            public bool UseSsl;
        }

        /// <summary>
        /// Pointer The USB_COMPANION_INFO structure provides information about a USB companion.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PIP_COMPANION_INFO
        {
            /// <summary>
            /// Bluetooth address of the companion, eg. "547F54ABCDEF"
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
            public IP_COMPANION_INFO[] IP;
        }

        /// <summary>
        /// Receive a message from the terminal
        /// </summary>
        /// <param name="outBuffer">pointer to the buffer that will receive the message received</param>
        /// <param name="outBufferSize">Size of reception buffer</param>
        /// <param name="bytesReceived">	Number of bytes received (If no message received, the value returned in this variable is 0)</param>
        /// <returns>TRUE if the service received the message from the terminal, FALSE if the received failed</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool receiveMessage(byte[] outBuffer, UInt32 outBufferSize, ref UInt32 bytesReceived);

        /// <summary>
        /// Send message to the terminal
        /// </summary>
        /// <param name="inBuffer">	pointer to the buffer containing the message to send to the terminal</param>
        /// <param name="inBufferSize">Size of buffer to send</param>
        /// <param name="bytesSent">Number of bytes sent, if the message cannot be sent the result is 0</param>
        /// <returns>TRUE if the service send the message to the terminal, FALSE if the sending failed</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool sendMessage(string inBuffer, UInt32 inBufferSize, ref UInt32 bytesSent);

        /// <summary>
        /// Retrieves the list of Usb Ingenico companions plugged in the Windows device.
        /// </summary>
        /// <param name="pCompanions">Buffer containing the list of companions plugged in the Windows device</param>
        /// <param name="pdwSize">Size of pCompanions buffer as input. Required size as output.</param>
        /// <param name="pdwEntries">Number of USB_COMPANION_INFO entries returned in pCompanions</param>
        /// <returns>ERROR_SUCCESS is returned in case of success and pCompanions contains the list of plugged companions. 
        ///         The number of companions in this list is indicated by *pdwEntries. 
        ///         ERROR_NOT_ENOUGH_MEMORY is returned if pCompanions is NULL or if its size is not large enough to contain all the plugged companions, 
        ///         *pdwSize contains the size of the buffer to be allocated. 
        ///         If *pdwSize equals zero it means that no companion is plugged..</returns>
        [DllImport("PclUtilities.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern int getUSBDevices(ref PUSB_COMPANION_INFO pCompanions, ref int pdwSize, ref int pdwEntries);

        /// <summary>
        /// Retrieves the list of Rs232 Ingenico companions plugged in the Windows device.
        /// </summary>
        /// <param name="pCompanions">Buffer containing the list of companions plugged in the Windows device</param>
        /// <param name="pdwSize">Size of pCompanions buffer as input. Required size as output.</param>
        /// <param name="pdwEntries">Number of USB_COMPANION_INFO entries returned in pCompanions</param>
        /// <returns>ERROR_SUCCESS is returned in case of success and pCompanions contains the list of plugged companions. 
        ///         The number of companions in this list is indicated by *pdwEntries. 
        ///         ERROR_NOT_ENOUGH_MEMORY is returned if pCompanions is NULL or if its size is not large enough to contain all the plugged companions, 
        ///         *pdwSize contains the size of the buffer to be allocated. 
        ///         If *pdwSize equals zero it means that no companion is plugged..</returns>
        [DllImport("PclUtilities.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern int getRS232Devices(ref PUSB_COMPANION_INFO pCompanions, ref int pdwSize, ref int pdwEntries);

        /// <summary>
        /// Get Bluetooth paired companions.
        /// </summary>
        /// <param name="pCompanions">A pointer to a buffer that, on output, receives an array of BLUETOOTH_COMPANION_INFO structures, 
        ///                           one for each Bluetooth paired companion.</param>
        /// <param name="pdwSize">Pointer to a variable that, on input, contains the size, in bytes, of the buffer specified by pCompanions. 
        ///                       On output, contains the size, in bytes, of the array of BLUETOOTH_COMPANION_INFO
        ///                       structures required for Bluetooth paired companions.</param>
        /// <param name="pdwEntries">Pointer to a variable that receives the number of Bluetooth paired 
        ///                          companions written to the buffer specified by pCompanions.</param>
        /// <returns>ERROR_SUCCESS is returned in case of success and pCompanions contains the list of plugged companions. 
        ///          The number of companions in this list is indicated by *pdwEntries. 
        ///          ERROR_NOT_ENOUGH_MEMORY is returned if pCompanions is NULL or if its size is not large enough 
        ///          to contain all the plugged companions, *pdwSize contains the size of the buffer to be allocated. 
        ///          If *pdwSize equals zero it means that no companion is plugged..
        /// </returns>
        [DllImport("PclUtilities.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern int getPairedDevices(ref PBLUETOOTH_COMPANION_INFO pCompanions, ref int pdwSize, ref int pdwEntries);


        /// <summary>
        /// Retrieves the list of IP Ingenico companions on the same network of the Windows device.
        /// </summary>
        /// <param name="pCompanions">Buffer containing the list of companions on the same network of the Windows device (can not be NULL)..</param>
        /// <param name="pdwSize">	Size of pCompanions buffer as input. Required size as output (can not be NULL)..</param>
        /// <param name="pdwEntries">Number of IP_COMPANION_INFO entries returned in pCompanions.</param>
        /// <returns>ERROR_SUCCESS is returned in case of success and pCompanions contains the list of plugged companions. 
        ///          The number of companions in this list is indicated by *pdwEntries. If *pdwEntries equals zero it means that no terminal is founded. 
        ///          ERROR_NOT_ENOUGH_MEMORY is returned if pCompanions size is not large enough to contain all the plugged companions, 
        ///          *pdwSize contains the size of the buffer to be allocated. ERROR_PCL_UTIL_IP_RECV_ERROR is returned in others error case
        /// </returns>
        [DllImport("PclUtilities.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern int getIPDevices(ref PIP_COMPANION_INFO pCompanions, ref int pdwSize, ref int pdwEntries);

        /// <summary>
        /// API activate companion com on USB.
        /// </summary>
        /// <param name="pCom">[in,out]	pCom	Null-terminated string containing the COM port of the companion to be paired.</param>
        /// <returns>TRUE in case of success FALSE in case of failure. 
        ///          Call GetLastError to get the error code. Possible error codes are Windows system error codes 
        ///          or the following specific PCL error codes: 
        ///          ERROR_PCL_UTIL_NO_COM_PORT No COM port is associated with companion. 
        ///          This may be due to issue during pairing. 
        ///          ERROR_PCL_UTIL_INVALID_COM_PORT COM port associated with companion is invalid. 
        ///          This may be due to issue during pairing. 
        ///          ERROR_PCL_UTIL_NO_RAS_ENTRY Error during RAS entry creation
        /// </returns>
        [DllImport("PclUtilities.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool activateUSBDevice(string pCom);

        /// <summary>
        /// Activate a Rs232 terminal plugged on the Windows device.
        /// </summary>
        /// <param name="pCom">[in,out]	pCom	Null-terminated string containing the COM port of the companion to be paired.</param>
        /// <returns>TRUE in case of success FALSE in case of failure. Call GetLastError to get the error code.
        ///          Possible error codes are Windows system error codes or the following specific PCL error codes: 
        ///          ERROR_PCL_UTIL_NO_COM_PORT No COM port is associated with terminal. This may be due to issue during pairing. 
        ///          ERROR_PCL_UTIL_INVALID_COM_PORT COM port associated with terminal is invalid. This may be due to issue during pairing. 
        ///          ERROR_PCL_UTIL_NO_RAS_ENTRY Error during RAS entry creation
        /// </returns>
        [DllImport("PclUtilities.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool activateRS232Device(string pCom);


        /// <summary>
        /// Activate Bluetooth terminal paired with the Windows device.
        /// </summary>
        /// <param name="pCom">[in,out]	pCom	Null-terminated string containing the COM port of the companion to be paired.</param>
        /// <returns>TRUE in case of success FALSE in case of failure. Call GetLastError to get the error code. 
        ///          Possible error codes are Windows system error codes or the following specific PCL error codes: 
        ///          ERROR_PCL_UTIL_NO_COM_PORT No COM port is associated with terminal. This may be due to issue during pairing. 
        ///          ERROR_PCL_UTIL_INVALID_COM_PORT COM port associated with terminal is invalid. This may be due to issue during pairing.
        ///          ERROR_PCL_UTIL_NO_RAS_ENTRY Error during RAS entry creation
        /// </returns>
        [DllImport("PclUtilities.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool activateBTCompanion(string pCom);

        /// <summary>
        /// Activate Bluetooth terminal paired with the Windows device.
        /// </summary>
        /// <param name="pCom">[in,out]	pCom	Null-terminated string containing the COM port of the companion to be paired.</param>
        /// <returns>TRUE in case of success FALSE in case of failure. Call GetLastError to get the error code. 
        ///          Possible error codes are Windows system error codes or the following specific PCL error codes: 
        ///          ERROR_PCL_UTIL_NO_COM_PORT No COM port is associated with terminal. This may be due to issue during pairing. 
        ///          ERROR_PCL_UTIL_INVALID_COM_PORT COM port associated with terminal is invalid. This may be due to issue during pairing.
        ///          ERROR_PCL_UTIL_NO_RAS_ENTRY Error during RAS entry creation
        /// </returns>
        [DllImport("PclUtilities.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool activateIPDevice(ref IP_COMPANION_INFO pCompanions);



        //[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        //public abstract class ITransactionIn
        //{
        //    public abstract bool setAmount([MarshalAs(UnmanagedType.LPWStr)] string amount);
        //    public abstract string getAmount();
        //    public abstract bool setTermNum([MarshalAs(UnmanagedType.LPWStr)] string termNum);
        //    public abstract string getTermNum();
        //    public abstract bool setCurrencyCode([MarshalAs(UnmanagedType.LPWStr)] string currencyCode);
        //    public abstract string getCurrencyCode();
        //    public abstract bool setOperation([MarshalAs(UnmanagedType.LPWStr)] string operation);
        //    public abstract string getOperation();
        //    public abstract bool setAuthorizationType([MarshalAs(UnmanagedType.LPWStr)] string type);
        //    public abstract string getAuthorizationType();
        //    public abstract bool setCtrlCheque([MarshalAs(UnmanagedType.LPWStr)] string ctrlCheque);
        //    public abstract string getCtrlCheque();
        //    public abstract bool setUserData1([MarshalAs(UnmanagedType.LPWStr)] string data);
        //    public abstract string getUserData1();
        //}

        //[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        //public abstract class ITransactionOut
        //{
        //    public abstract string getAmount();
        //    public abstract string getC3Error();
        //    public abstract string getCurrencyCode();
        //    public abstract string getTerminalNumber();
        //    public abstract string getUserData1();
        //    public abstract string getPrivateData();
        //}

        /// <summary>
        /// Create a instance of ITransactionIn
        /// </summary>
        /// <returns>Pointer to a ITransactionIn, you need delete this pointer after use</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern IntPtr create_itransactionin();

        /// <summary>
        /// Create a instance of ITransactionOut
        /// </summary>
        /// <returns>Pointer to a ITransactionIn, you need delete this pointer after use</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern IntPtr create_itransactionout();

        /// <summary>
        /// Launches a transaction request with extended parameters, 
        /// passing the input parameters to the companion payment application. 
        /// The output parameters are filled with the response from the companion payment application.
        /// </summary>
        /// <param name="transactionReq">Transaction request parameters</param>
        /// <param name="transactionResp">	Transaction response parameters. Call getC3Error to check transaction result.</param>
        /// <param name="nApplicationNumber">Specifies explicitely the Telium application to run on the companion 
        ///                                 (this application must implements PDA_START_TRANSACTION service)</param>
        /// <param name="inBuffer">Pointer to the input extended data</param>
        /// <param name="inBufferSize">	Size of the input extended data</param>
        /// <param name="outBuffer">Pointer to the output extended data</param>
        /// <param name="outBufferSize">On input, size of the output buffer. 
        ///                             On output, size of the extended data received from the companion.</param>
        /// <returns>TRUE in case of success, FALSE in case of failure</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern bool doTransactionEx([In, Out] IntPtr transactionReq, [In, Out] IntPtr transactionResp,
                                                int nApplicationNumber, byte[] inBuffer, int inBufferSize, byte[] outBuffer, ref int outBufferSize);

        /// <summary>
        /// Launches a transaction request, passing the input parameters to the companion payment application. 
        /// The output parameters are filled with the response from the companion payment application.
        /// </summary>
        /// <param name="transactionReq">Transaction request parameters</param>
        /// <param name="transactionResp">Transaction response parameters. Call getC3Error to check transaction result.</param>
        /// <returns>RUE in case of success, FALSE in case of failure</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool doTransaction([In, Out] IntPtr transactionReq, [In, Out] IntPtr transactionResp);

        /// <summary>
        /// Add dynamic bridge. You can add up to 12 bridges.
        /// </summary>
        /// <param name="port">TCP port</param>
        /// <param name="direction">0 means Windows connects to Telium, 1 means Telium connects to Windows</param>
        /// <returns>0 if success else negative error code: 
        ///         -1: No more dynamic bridges available 
        ///         -2: Bridge already exists. This can be considered as information and is not a real error. 
        ///         -3: Issue during thread creation. 
        ///         -4: Bridge initialization failed. 
        ///         .-5: PCL service is not started.
        /// </returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern int addDynamicBridge(int port, int direction);

        /// <summary>
        /// Add dynamic bridge on local interface. You can add up to 12 bridges.
        /// </summary>
        /// <param name="port">TCP port</param>
        /// <param name="direction">0 means Windows connects to Telium, 1 means Telium connects to Windows</param>
        /// <returns>0 if success else negative error code: 
        ///         -1: No more dynamic bridges available 
        ///         -2: Bridge already exists. This can be considered as information and is not a real error. 
        ///         -3: Issue during thread creation. 
        ///         -4: Bridge initialization failed. 
        ///         .-5: PCL service is not started.
        /// </returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern int addDynamicBridgeLocal(int port, int direction);

        [DllImport("PCL_Bridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern IntPtr PCL_Bridge_Create_ITransactionIn([MarshalAs(UnmanagedType.AnsiBStr)] string Amount,
                                                                     [MarshalAs(UnmanagedType.AnsiBStr)] string AuthorizationType,
                                                                     [MarshalAs(UnmanagedType.AnsiBStr)] string CtrlCheque,
                                                                     [MarshalAs(UnmanagedType.AnsiBStr)] string CurrencyCode,
                                                                     [MarshalAs(UnmanagedType.AnsiBStr)] string Operation,
                                                                     [MarshalAs(UnmanagedType.AnsiBStr)] string TermNum,
                                                                     [MarshalAs(UnmanagedType.AnsiBStr)] string UserData1);

        [DllImport("PCL_Bridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern IntPtr PCL_Bridge_Create_ITransactionOut();

        [DllImport("PCL_Bridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern void PCL_Bridge_Delete_ITransactionOut(IntPtr iTransOut);

        [DllImport("PCL_Bridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern void PCL_Bridge_Delete_ITransactionIn(IntPtr iTransIn);


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public class PCL_Bridge_In_Get_Parameters
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string Amount;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string AuthorizationType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string CtrlCheque;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string CurrencyCode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string Operation;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string TermNum;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string UserData1;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public class PCL_Bridge_Out_Get_Parameters
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string Amount;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string C3Error;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string CurrencyCode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string TerminalNumber;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
            public string UserData1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 200)]
            public string PrivateData;
        }

        //[DllImport("PCL_Bridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        //public static extern void PCL_Bridge_GetIn_Parameters(IntPtr iTransIn, [In, Out]  PCL_Bridge_In_Get_Parameters Parameters);

        //[DllImport("PCL_Bridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        //public static extern void PCL_Bridge_GetOut_Parameters(IntPtr iTransOut, [In, Out]  PCL_Bridge_Out_Get_Parameters Parameters);        


        /// <summary>
        /// Open Barcode with default inactivity timeout (600s)
        /// </summary>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if the shortcut was launched, FALSE otherwise</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool openBarcode(StringBuilder result);

        /// <summary>
        /// Close Barcode
        /// </summary>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool closeBarcode(StringBuilder result);

        /// <summary>
        /// Start Barcode scan.
        /// </summary>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool bcrStartScan(StringBuilder result);

        /// <summary>
        /// Stop Barcode scan.
        /// </summary>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool bcrStopScan(StringBuilder result);

        /// <summary>
        /// Configure Barcode reader mode.
        /// </summary>
        /// <param name="modeType">0=single scan and 1=multiple scan</param>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool bcrSetReaderMode(int modeType, StringBuilder result);

        /// <summary>
        /// Configure Barcode good scan beep.
        /// </summary>
        /// <param name="modeBeep">0=disable scan, 1=one beep and 2=two beep</param>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool bcrSetGoodScanBeep(int modeBeep, StringBuilder result);

        /// <summary>
        /// Configure Barcode lighting mode.
        /// </summary>
        /// <param name="mode">0= illumination priority, 1= aperture priority.</param>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool bcrSetLightingMode(int mode, StringBuilder result);

        /// <summary>
        /// Configure Barcode imager mode.
        /// </summary>
        /// <param name="mode">0=1D, 1=1D 2D, 2=1D 2D bright and 3=1D 2D reflective</param>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool bcrSetImagerMode(int mode, StringBuilder result);

        /// <summary>
        /// Configure enable symbologies on Barcode.
        /// </summary>
        /// <param name="symbologies">Array containing barcode symbologies to enable.</param>
        /// <param name="symbologyCount">Size of symbologies array.</param>
        /// <param name="result">status of the operation</param>
        /// <returns></returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool bcrEnableSymbologies(int[] symbologies, int symbologyCount, StringBuilder result);

        /// <summary>
        /// This callback allows retrieving data scanned with Companion barcode reader.
        /// </summary>
        /// <param name="barcode">buffer containing barcode reader data</param>
        /// <param name="barcodelen">buffer length</param>
        /// <param name="symbology">buffer containing symbologie of barcode</param>
        /// <param name="symbologylen">buffer length</param>
        public delegate void BarcodeEventExtFunc(StringBuilder barcode, int barcodelen, StringBuilder symbology, int symbologylen);

        /// <summary>
        /// Register barcode extention callback
        /// </summary>
        /// <param name="CallbackFunction">function</param>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern void registerBarcodeEventExtCallback(BarcodeEventExtFunc CallbackFunction);

        /// <summary>
        /// This callback is called when the barcode reader is automatically closed on inactivity timeout expiration
        /// </summary>
        /// <returns>0 if successful</returns>
        public delegate int BarcodeEventCloseFunc();

        /// <summary>
        /// Register callback functions to handle signature capture, barcode events and printing from Telium
        /// </summary>
        /// <param name="sCallbacks">structure with all functions callbacks</param>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern void registerCallbacks(BarcodeEventCloseFunc sCallbacks);

        /// <summary>
        /// Open Printer
        /// </summary>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool openPrinter(StringBuilder result);

        /// <summary>
        /// Close Printer
        /// </summary>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool closePrinter(StringBuilder result);

        /// <summary>
        /// Close Printer
        /// </summary>
        /// <param name="pbString">Buffer to be printed</param>
        /// <param name="result">status of the operation</param>
        /// <returns>TRUE if success else return FALSE</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool printText(byte[] pbString, StringBuilder result);

        /// <summary>
        /// Get logs state.
        /// </summary>
        /// <returns>true is logs are enabled.</returns>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern bool isDebugLogEnabled();

        /// <summary>
        /// Enable log
        /// </summary>
        /// <param name="enable">If true, enable logs.</param>
        [DllImport("PCLService.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        public static extern void enableDebugLog(bool enable);
    }
}
