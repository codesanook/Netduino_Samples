using FoodDehydrator3000.Properties;
using Maple;
using Microsoft.SPOT;
using H = Microsoft.SPOT.Hardware;
using Microsoft.SPOT.Net.NetworkInformation;
using Netduino.Foundation.Displays;
using Netduino.Foundation.Displays.LCD;
using Netduino.Foundation.Displays.TextDisplayMenu;
using Netduino.Foundation.Generators;
using Netduino.Foundation.Relays;
using Netduino.Foundation.Sensors;
using Netduino.Foundation.Sensors.Rotary;
using Netduino.Foundation.Sensors.Temperature;
using Netduino.Foundation.ICs.IOExpanders.MCP23008;
using System;
using System.Collections;
using N = SecretLabs.NETMF.Hardware.Netduino;
using Netduino.Foundation;
using Netduino.Foundation.Sensors.Buttons;

namespace FoodDehydrator3000
{
    public class App
    {
        // peripherals
        protected PushButton _pushButton = null;
        protected AnalogTemperature _tempSensor = null;
        protected SoftPwm _heaterRelayPwm = null;
        protected Relay _fanRelay = null;
        protected PushButton _button = null;
        protected ITextDisplay _display = null;

        RotaryEncoderWithButton _encoder = null;

        // controllers
        protected DehydratorController _dehydrator = null;

        // vars
        protected NetworkInterface[] _interfaces;
        protected float _currentTemp;
        protected MapleServer _server;
        protected Menu _menu;
        protected DateTime _tempUpdated;
        protected float _targetTemp;
        protected TimeSpan _runTime;
        protected bool _inMenu = false;

        public App()
        {
            // initialze
            this.InitializePeripherals();
            this.InitializeMenu();

            // initilaize our dehydrator controller
            _dehydrator = new DehydratorController(_tempSensor, _heaterRelayPwm, _fanRelay, _display);

            // show our info screen
            this.DisplayInfoScreen();

            // 
            this.WireUpPeripheralEvents();

            // setup our web server
            this.InitializeWebServer();
        }

        /// <summary>
        /// Configures the hardware perihperals (LCD, temp sensor, relays, etc.) 
        /// so they can be used by the application.
        /// </summary>
        protected void InitializePeripherals()
        {
            // pushbutton (for testing)
            _pushButton = new PushButton(
                (H.Cpu.Pin)0x15, CircuitTerminationType.Floating);

            // Rotary Encoder
            _encoder = new RotaryEncoderWithButton(
                N.Pins.GPIO_PIN_D7, N.Pins.GPIO_PIN_D6, N.Pins.GPIO_PIN_D5,
                CircuitTerminationType.CommonGround);

            // LCD
            //_display = new Lcd2004(new MCP23008());
            _display = new Lcd2004(N.Pins.GPIO_PIN_D8, N.Pins.GPIO_PIN_D9, N.Pins.GPIO_PIN_D10, N.Pins.GPIO_PIN_D11, N.Pins.GPIO_PIN_D12, N.Pins.GPIO_PIN_D13);
            _display.Clear();
            Debug.Print("Display up.");
            _display.WriteLine("Display up!", 0);

            // Analog Temp Sensor. Setup to notify at half a degree changes
            _tempSensor = new AnalogTemperature(N.AnalogChannels.ANALOG_PIN_A0,
                AnalogTemperature.KnownSensorType.LM35, temperatureChangeNotificationThreshold: 0.5F);
            Debug.Print("TempSensor up.");
            _display.WriteLine("Temp Sensor up!", 1);

            // Heater driven by Software PWM
            _heaterRelayPwm = new SoftPwm(N.Pins.GPIO_PIN_D2, 0.5f, 1.0f / 30.0f);
            Debug.Print("Heater PWM up.");
            _display.WriteLine("Heater PWM up!", 2);

            // Fan Relay
            _fanRelay = new Relay(N.Pins.GPIO_PIN_D3);
            Debug.Print("Fan up.");
            _display.WriteLine("Fan up!", 3);

            // output status
            Debug.Print("Peripherals up");
            _display.WriteLine("Peripherals online!", 0);
        }

        //
        protected void WireUpPeripheralEvents()
        {
            // Analog Temp Sensor. Setup to notify at half a degree changes
            _tempSensor.TemperatureChanged += (object sender, SensorFloatEventArgs e) => {
                UpdateTemp(e.CurrentValue);
            };

            _pushButton.LongPressClicked += (s, e) => {
                Debug.Print("Long press.");
                _targetTemp = 40;
                TogglePower();
            };

            _encoder.Clicked += (s, e) =>
            {
                // if the menu isn't displayed, display it. otherwise
                // encoder click events are handled by menu
                if (!_inMenu) {
                    this.DisplayMenu();
                }
            };
        }

        protected void InitializeMenu()
        {
            // initialize menu
            _menu = new Menu(_display, _encoder, Resources.GetBytes(Resources.BinaryResources.menu));
            _menu.ValueChanged += HandleMenuValueChange;
            _menu.Selected += HandleMenuSelected;
        }

        protected void InitializeWebServer()
        {
            // configure our web server
            RequestHandler handler = new RequestHandler();
            handler.TurnOff += Handler_TurnOff;
            handler.TurnOn += Handler_TurnOn;
            handler.GetStatus += Handler_GetStatus;

            _server = new MapleServer();
            _server.AddHandler(handler);
        }

        /// <summary>
        /// Closes the menu (if open), and displays the info screen which 
        /// has temp and such on it.
        /// </summary>
        protected void DisplayInfoScreen()
        {
            if(_inMenu) CloseMenu();
            
            _display.WriteLine("Current Temp: " + _tempSensor.Temperature.ToString("F1") + "C", 0);
            _display.WriteLine("Target:" + _targetTemp.ToString("F0") + "C", 1);
            var remainingTime = _dehydrator.RunningTimeLeft;
            _display.WriteLine("Time: " + PadLeft(remainingTime.Hours.ToString(), '0', 2) + ":" + PadLeft(remainingTime.Minutes.ToString(), '0', 2), 2);
            _display.WriteLine("Click for more.", 3);
        }

        /// <summary>
        /// Displays the menu.
        /// </summary>
        protected void DisplayMenu()
        {
            this._inMenu = true;
            this._menu.Enable();
        }

        /// <summary>
        /// Closes the menu and displays the info screen.
        /// </summary>
        protected void CloseMenu()
        {
            this._menu.Disable();
            this.DisplayInfoScreen();
            this._inMenu = false;
        }

        /// <summary>
        /// Called when an item in the menu is selected.
        /// </summary>
        protected void HandleMenuSelected(object sender, MenuSelectedEventArgs e)
        {
            switch (e.Command)
            {
                case "returnToInfo":
                    this.DisplayInfoScreen();
                    break;
                case "power":
                    Debug.Print("menu power");
                    TogglePower();
                    break;
            }
        }

        /// <summary>
        /// Called when an item in the menu changes.
        /// </summary>
        protected void HandleMenuValueChange(object sender, ValueChangedEventArgs e)
        {
            if(e.ItemID == "temperature")
            {
                _targetTemp = (float)(double)e.Value; //smh
                _dehydrator.TargetTemperature = _targetTemp;
                _menu.UpdateItemValue("displayTargetTemp", e.Value);
            }
            else if(e.ItemID == "timer")
            {
                _runTime = (TimeSpan)e.Value;
            }
        }

        /// <summary>
        /// Called when the temperature data has changed. 
        /// Updates the display and menu data.
        /// </summary>
        /// <param name="temp"></param>
        protected void UpdateTemp(float temp)
        {
            int updateInterval = 5;

            if (!_inMenu)
            {
                DisplayInfoScreen();
            }

            //if(_menu != null)
            //{
            //    if(DateTime.Now > _tempUpdated.AddSeconds(updateInterval))
            //    {
            //        Debug.Print("Update display");
            //        TimeSpan remainingTime = _dehydrator.RunningTimeLeft;

            //        Hashtable values = new Hashtable();
            //        values.Add("displayCurrentTemp", temp);
            //        values.Add("temperature", _targetTemp);
            //        values.Add("displayRemainingTime", PadLeft(remainingTime.Hours.ToString(), '0', 2) + ":" + PadLeft(remainingTime.Minutes.ToString(), '0', 2));
            //        _menu.UpdateItemValue(values);
            //        _tempUpdated = DateTime.Now;
            //    }
            //}
        }

        protected void TogglePower()
        {
            if (_dehydrator.Running)
            {
                Debug.Print("PowerButtonClicked, _running == true, turning off.");
                _dehydrator.TurnOff(10);
            }
            else
            {
                Debug.Print("PowerButtonClicked, _running == false, turning on.");
                _dehydrator.TurnOn(_targetTemp, _runTime); // set to 35C to start
            }
        }

        public void Run()
        {
            bool networkInit = Netduino.Foundation.Network.Initializer.InitializeNetwork("http://google.com");

            if (networkInit)
            {
                _server.Start();
                Debug.Print("Maple server started.");
            }
        }

        public void Stop()
        {
            _server.Stop();
            Debug.Print("Maple server stopped.");
        }

        private float Handler_GetStatus()
        {
            if (_dehydrator.Running)
            {
                return _currentTemp;
            }
            else
            {
                return -1;
            }
        }

        private void Handler_TurnOn(int targetTemp)
        {
            _dehydrator.TurnOn(targetTemp);
        }

        private void Handler_TurnOff(int coolDownDelay)
        {
            _dehydrator.TurnOff(coolDownDelay);
        }

        public static string PadLeft(string text, char filler, int size)
        {
            string padded = string.Empty;
            for (int i = text.Length; i < size; i++)
            {
                padded += filler;
            }
            return padded + text;
        }
    }
}
