namespace sGBA;

public class TimerController
{
	public GbaSystem Gba { get; }
	public TimerChannel[] Channels = new TimerChannel[4];
	public long NextGlobalEvent = long.MaxValue;

	private static readonly int[] PrescaleBits = [0, 6, 8, 10];

	public TimerController( GbaSystem gba )
	{
		Gba = gba;
		for ( int i = 0; i < 4; i++ )
			Channels[i] = new TimerChannel( i );
	}

	public void Reset()
	{
		for ( int i = 0; i < 4; i++ )
			Channels[i].Reset();
		NextGlobalEvent = long.MaxValue;
	}

	public void WriteControl( int idx, ushort value )
	{
		var c = Channels[idx];
		bool wasEnabled = c.Enabled;
		bool wasCountUp = c.CountUp;

		if ( wasEnabled && !wasCountUp )
		{
			SyncCounter( idx );
		}

		c.Control = value;
		c.Enabled = (value & 0x80) != 0;
		c.CountUp = idx > 0 && (value & 0x04) != 0;
		c.IrqEnable = (value & 0x40) != 0;
		c.PrescalerIndex = value & 3;

		bool reschedule = false;

		if ( wasEnabled != c.Enabled )
		{
			reschedule = true;
			if ( c.Enabled )
			{
				c.Counter = c.Reload;
			}
		}
		else if ( wasCountUp != c.CountUp )
		{
			reschedule = true;
		}
		else if ( c.Enabled && !c.CountUp )
		{
			reschedule = true;
		}

		if ( reschedule )
		{
			c.NextOverflowCycle = long.MaxValue;
			if ( c.Enabled && !c.CountUp )
			{
				int bits = PrescaleBits[c.PrescalerIndex];
				long tickMask = (1L << bits) - 1;
				c.LastEventCycle = Gba.Cpu.Cycles & ~tickMask;
				ScheduleOverflow( c );
			}
			RecalcGlobalEvent();
		}
	}

	public void RecalcGlobalEvent()
	{
		long min = long.MaxValue;
		for ( int i = 0; i < 4; i++ )
		{
			var ch = Channels[i];
			if ( ch.Enabled && !ch.CountUp && ch.NextOverflowCycle < min )
				min = ch.NextOverflowCycle;
		}
		NextGlobalEvent = min;
	}

	private void ScheduleOverflow( TimerChannel c )
	{
		int bits = PrescaleBits[c.PrescalerIndex];
		long tickMask = (1L << bits) - 1;
		long ticksToOverflow = (long)(0x10000 - c.Counter) << bits;
		c.NextOverflowCycle = (c.LastEventCycle & ~tickMask) + ticksToOverflow;
	}

	private void SyncCounter( int idx )
	{
		var c = Channels[idx];
		if ( !c.Enabled || c.CountUp ) return;

		int bits = PrescaleBits[c.PrescalerIndex];
		long tickMask = (1L << bits) - 1;
		long currentCycle = Gba.Cpu.Cycles & ~tickMask;
		long ticks = (currentCycle - c.LastEventCycle) >> bits;
		c.LastEventCycle = currentCycle;

		if ( ticks <= 0 ) return;

		long total = (long)c.Counter + ticks;
		int reload = c.Reload;
		int range = 0x10000 - reload;

		while ( total >= 0x10000 )
		{
			if ( range <= 0 ) { total = reload; break; }
			total -= range;
		}

		c.Counter = (ushort)total;
	}

	public ushort GetCounter( int idx )
	{
		var c = Channels[idx];
		if ( !c.Enabled || c.CountUp ) return c.Counter;

		int bits = PrescaleBits[c.PrescalerIndex];
		long tickMask = (1L << bits) - 1;
		long adjustedCycle = (Gba.Cpu.Cycles - 2) & ~tickMask;
		long ticks = (adjustedCycle - c.LastEventCycle) >> bits;

		int result = (int)c.Counter + (int)ticks;

		int reload = c.Reload;
		int range = 0x10000 - reload;
		while ( result >= 0x10000 )
		{
			if ( range <= 0 ) { result = reload; break; }
			result -= range;
		}

		return (ushort)(result & 0xFFFF);
	}

	public void Tick( int cycles )
	{
		long currentCycle = Gba.Cpu.Cycles;

		if ( currentCycle < NextGlobalEvent )
			return;

		for ( int i = 0; i < 4; i++ )
		{
			var c = Channels[i];
			if ( !c.Enabled || c.CountUp ) continue;

			while ( currentCycle >= c.NextOverflowCycle )
			{
				long overflowCycle = c.NextOverflowCycle;
				c.Counter = c.Reload;
				c.LastEventCycle = overflowCycle;

				int bits = PrescaleBits[c.PrescalerIndex];
				long ticksToOverflow = (long)(0x10000 - c.Counter) << bits;
				if ( ticksToOverflow <= 0 ) ticksToOverflow = 1;
				c.NextOverflowCycle = overflowCycle + ticksToOverflow;

				if ( c.IrqEnable )
				{
					int late = (int)(currentCycle - overflowCycle);
					Gba.Io.RaiseIrq( (IrqFlag)(1 << (3 + i)), late );
				}

				if ( i <= 1 )
				{
					Gba.Apu.OnTimerOverflow( i );
				}

				if ( i < 3 )
				{
					var next = Channels[i + 1];
					if ( next.Enabled && next.CountUp )
					{
						IncrementCascade( i + 1, currentCycle );
					}
				}
			}
		}

		RecalcGlobalEvent();
	}

	private void IncrementCascade( int idx, long currentCycle )
	{
		var c = Channels[idx];
		c.Counter++;
		if ( c.Counter == 0 )
		{
			c.Counter = c.Reload;

			if ( c.IrqEnable )
			{
				Gba.Io.RaiseIrq( (IrqFlag)(1 << (3 + idx)), 0 );
			}

			if ( idx <= 1 )
			{
				Gba.Apu.OnTimerOverflow( idx );
			}

			if ( idx < 3 )
			{
				var next = Channels[idx + 1];
				if ( next.Enabled && next.CountUp )
				{
					IncrementCascade( idx + 1, currentCycle );
				}
			}
		}
	}
}

public class TimerChannel
{
	public int Index;
	public ushort Reload;
	public ushort Counter;
	public ushort Control;

	public bool Enabled;
	public bool CountUp;
	public bool IrqEnable;
	public int PrescalerIndex;

	public long LastEventCycle;
	public long NextOverflowCycle = long.MaxValue;

	public TimerChannel( int index )
	{
		Index = index;
	}

	public void Reset()
	{
		Reload = Counter = Control = 0;
		Enabled = CountUp = IrqEnable = false;
		PrescalerIndex = 0;
		LastEventCycle = 0;
		NextOverflowCycle = long.MaxValue;
	}
}
