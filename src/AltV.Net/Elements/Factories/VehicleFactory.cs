using System;
using AltV.Net.Elements.Entities;

namespace AltV.Net.Elements.Factories
{
    public class VehicleFactory : IEntityFactory<IVehicle>
    {
        public IVehicle Create(IntPtr vehiclePointer)
        {
            return new Vehicle(vehiclePointer);
        }
    }
}