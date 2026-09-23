namespace RIoT2.Matter.Clusters;

/// <summary>
/// The features the Thermostat cluster advertises through its FeatureMap. Values match the Matter Core
/// Specification, section 4.3.4 (Thermostat feature map).
/// </summary>
[Flags]
public enum ThermostatFeature : uint
{
    /// <summary>No feature; not a valid thermostat configuration on its own.</summary>
    None = 0,

    /// <summary>HEAT: the thermostat is capable of managing a heating device.</summary>
    Heating = 0x01,

    /// <summary>COOL: the thermostat is capable of managing a cooling device.</summary>
    Cooling = 0x02,

    /// <summary>OCC: the thermostat supports unoccupied setpoints. Deferred by this implementation.</summary>
    Occupancy = 0x04,

    /// <summary>SCH: the thermostat supports weekly schedules. Deferred by this implementation.</summary>
    ScheduleConfiguration = 0x08,

    /// <summary>SB: the thermostat supports setback values. Deferred by this implementation.</summary>
    Setback = 0x10,

    /// <summary>AUTO: the thermostat supports the automatic heating/cooling mode, and therefore a dead band.</summary>
    AutoMode = 0x20,
}

/// <summary>
/// The operating mode of the thermostat, carried by the SystemMode attribute as <c>enum8</c>. Values
/// match the Matter Core Specification, section 4.3.7.24 (SystemModeEnum).
/// </summary>
public enum ThermostatSystemMode : byte
{
    /// <summary>The thermostat is off; neither heating nor cooling runs.</summary>
    Off = 0,

    /// <summary>Heating and cooling are both engaged as needed; requires the AutoMode feature.</summary>
    Auto = 1,

    /// <summary>Cooling only.</summary>
    Cool = 3,

    /// <summary>Heating only.</summary>
    Heat = 4,

    /// <summary>Heating with an auxiliary/emergency heat source.</summary>
    EmergencyHeat = 5,

    /// <summary>Cooling ahead of a scheduled setpoint change.</summary>
    Precooling = 6,

    /// <summary>The fan runs without heating or cooling.</summary>
    FanOnly = 7,

    /// <summary>Dehumidification only.</summary>
    Dry = 8,

    /// <summary>The thermostat runs its sleep profile.</summary>
    Sleep = 9,
}

/// <summary>
/// What the thermostat is actually running right now, reported by the ThermostatRunningMode attribute as
/// <c>enum8</c>. Values match the Matter Core Specification, section 4.3.7.25 (ThermostatRunningModeEnum),
/// which is the SystemMode set restricted to Off / Cool / Heat.
/// </summary>
public enum ThermostatRunningMode : byte
{
    /// <summary>Neither heating nor cooling is running.</summary>
    Off = 0,

    /// <summary>Cooling is running.</summary>
    Cool = 3,

    /// <summary>Heating is running.</summary>
    Heat = 4,
}

/// <summary>
/// The heating/cooling capability the thermostat is wired for, carried by the
/// ControlSequenceOfOperation attribute as <c>enum8</c>. Values match the Matter Core Specification,
/// section 4.3.7.19 (ControlSequenceOfOperationEnum).
/// </summary>
public enum ThermostatControlSequence : byte
{
    /// <summary>Cooling only.</summary>
    CoolingOnly = 0,

    /// <summary>Cooling with reheat.</summary>
    CoolingWithReheat = 1,

    /// <summary>Heating only.</summary>
    HeatingOnly = 2,

    /// <summary>Heating with reheat.</summary>
    HeatingWithReheat = 3,

    /// <summary>Both cooling and heating.</summary>
    CoolingAndHeating = 4,

    /// <summary>Both cooling and heating, with reheat.</summary>
    CoolingAndHeatingWithReheat = 5,
}

/// <summary>
/// Which setpoints the SetpointRaiseLower command shifts, transmitted as <c>enum8</c>. Values match the
/// Matter Core Specification, section 4.3.9.1 (SetpointRaiseLowerModeEnum).
/// </summary>
public enum SetpointRaiseLowerMode : byte
{
    /// <summary>Adjust the heating setpoint only.</summary>
    Heat = 0,

    /// <summary>Adjust the cooling setpoint only.</summary>
    Cool = 1,

    /// <summary>Adjust both setpoints.</summary>
    Both = 2,
}
