using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace @Bifrost.@Tests.@Queues;

/// <summary>Checks the reviewed property over the supplied case domain.</summary>
public sealed class @PriorityVirtualTimePropertyTests
{
    /// <summary>Checks the property for fixture case 1.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_1() => Check(new long[] { 0L, 10000000L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 2.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_2() => Check(new long[] { 100L, 1L, 1L, 1L, 0L });

    /// <summary>Checks the property for fixture case 3.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_3() => Check(new long[] { 100L, 1L, 1L, 1L, 2L });

    /// <summary>Checks the property for fixture case 4.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_4() => Check(new long[] { 100L, 5000000L, 5000000L, 1L, 0L });

    /// <summary>Checks the property for fixture case 5.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_5() => Check(new long[] { 100L, 5000000L, 5000000L, 1L, 2L });

    /// <summary>Checks the property for fixture case 6.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_6() => Check(new long[] { 100L, 9999999L, 9999999L, 1L, 0L });

    /// <summary>Checks the property for fixture case 7.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_7() => Check(new long[] { 100L, 9999999L, 9999999L, 1L, 2L });

    /// <summary>Checks the property for fixture case 8.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_8() => Check(new long[] { 100L, 10000000L, 10000000L, 1L, 0L });

    /// <summary>Checks the property for fixture case 9.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_9() => Check(new long[] { 100L, 10000000L, 10000000L, 1L, 2L });

    /// <summary>Checks the property for fixture case 10.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_10() => Check(new long[] { 100L, 10000001L, 10000001L, 1L, 0L });

    /// <summary>Checks the property for fixture case 11.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_11() => Check(new long[] { 100L, 10000001L, 10000001L, 1L, 2L });

    /// <summary>Checks the property for fixture case 12.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_12() => Check(new long[] { 100L, 15000000L, 15000000L, 1L, 0L });

    /// <summary>Checks the property for fixture case 13.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_13() => Check(new long[] { 100L, 15000000L, 15000000L, 1L, 2L });

    /// <summary>Checks the property for fixture case 14.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_14() => Check(new long[] { 100L, 19999999L, 19999999L, 1L, 0L });

    /// <summary>Checks the property for fixture case 15.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_15() => Check(new long[] { 100L, 19999999L, 19999999L, 1L, 2L });

    /// <summary>Checks the property for fixture case 16.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_16() => Check(new long[] { 100L, 20000000L, 20000000L, 1L, 0L });

    /// <summary>Checks the property for fixture case 17.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_17() => Check(new long[] { 100L, 20000000L, 20000000L, 1L, 2L });

    /// <summary>Checks the property for fixture case 18.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_18() => Check(new long[] { 50L, 3333333L, 3333333L, 3L, 0L });

    /// <summary>Checks the property for fixture case 19.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_19() => Check(new long[] { 50L, 3333333L, 3333333L, 3L, 2L });

    /// <summary>Checks the property for fixture case 20.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_20() => Check(new long[] { 50L, 3333334L, 3333334L, 3L, 0L });

    /// <summary>Checks the property for fixture case 21.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_21() => Check(new long[] { 50L, 3333334L, 3333334L, 3L, 2L });

    /// <summary>Checks the property for fixture case 22.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_22() => Check(new long[] { 50L, 6666666L, 6666666L, 3L, 0L });

    /// <summary>Checks the property for fixture case 23.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_23() => Check(new long[] { 50L, 6666667L, 6666667L, 3L, 2L });

    /// <summary>Checks the property for fixture case 24.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_24() => Check(new long[] { 50L, 1428571L, 1428571L, 7L, 0L });

    /// <summary>Checks the property for fixture case 25.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_25() => Check(new long[] { 50L, 1428572L, 1428572L, 7L, 0L });

    /// <summary>Checks the property for fixture case 26.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_26() => Check(new long[] { 50L, 1428571L, 1428571L, 7L, 2L });

    /// <summary>Checks the property for fixture case 27.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_27() => Check(new long[] { 50L, 1428572L, 1428572L, 7L, 2L });

    /// <summary>Checks the property for fixture case 28.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_28() => Check(new long[] { 1000000L, 300000000L, 50000000L, 3579545L, 0L });

    /// <summary>Checks the property for fixture case 29.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_29() => Check(new long[] { 1000000L, 300000000L, 50000000L, 3579545L, 2L });

    /// <summary>Checks the property for fixture case 30.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_30() => Check(new long[] { 1000000L, 123456789L, 987654321L, 14318180L, 0L });

    /// <summary>Checks the property for fixture case 31.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_31() => Check(new long[] { 1000000L, 123456789L, 987654321L, 14318180L, 2L });

    /// <summary>Checks the property for fixture case 32.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_32() => Check(new long[] { 1000000L, 7777777L, 3333333L, 24000000L, 0L });

    /// <summary>Checks the property for fixture case 33.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_33() => Check(new long[] { 1000000L, 7777777L, 3333333L, 24000000L, 2L });

    /// <summary>Checks the property for fixture case 34.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_34() => Check(new long[] { 0L, 5000000L, 5000000L, 3L, 0L });

    /// <summary>Checks the property for fixture case 35.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_35() => Check(new long[] { 0L, 5000000L, 5000000L, 3L, 2L });

    /// <summary>Checks the property for fixture case 36.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_36() => Check(new long[] { 0L, 7500000L, 7500000L, 15000000L, 0L });

    /// <summary>Checks the property for fixture case 37.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_37() => Check(new long[] { 0L, 7500000L, 7500000L, 15000000L, 2L });

    /// <summary>Checks the property for fixture case 38.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_38() => Check(new long[] { 0L, 2L, 2L, 5000000L, 0L });

    /// <summary>Checks the property for fixture case 39.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_39() => Check(new long[] { 0L, 2L, 2L, 5000000L, 2L });

    /// <summary>Checks the property for fixture case 40.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_40() => Check(new long[] { 0L, 1L, 1L, 9999999L, 0L });

    /// <summary>Checks the property for fixture case 41.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_41() => Check(new long[] { 0L, 1L, 1L, 1000000000L, 0L });

    /// <summary>Checks the property for fixture case 42.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_42() => Check(new long[] { 0L, 1L, 1L, 1000000000L, 2L });

    /// <summary>Checks the property for fixture case 43.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_43() => Check(new long[] { 0L, 9007199254740993L, 9007199254740993L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 44.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_44() => Check(new long[] { 0L, 9007199254740993L, 9007199254740993L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 45.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_45() => Check(new long[] { 0L, 9223372036854775806L, 9223372036854775806L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 46.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_46() => Check(new long[] { 0L, 9223372036854775806L, 9223372036854775806L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 47.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_47() => Check(new long[] { 50000000000000L, 100000000000L, 100000000000L, 1000000000L, 0L });

    /// <summary>Checks the property for fixture case 48.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_48() => Check(new long[] { 50000000000000L, 100000000000L, 100000000000L, 1000000000L, 1L });

    /// <summary>Checks the property for fixture case 49.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_49() => Check(new long[] { 50000000000000L, 100000000000L, 100000000000L, 1000000000L, 2L });

    /// <summary>Checks the property for fixture case 50.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_50() => Check(new long[] { 0L, 92233720368547758L, 92233720368547758L, 1000000000L, 0L });

    /// <summary>Checks the property for fixture case 51.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_51() => Check(new long[] { 0L, 92233720368547758L, 92233720368547758L, 1000000000L, 2L });

    /// <summary>Checks the property for fixture case 52.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_52() => Check(new long[] { 0L, 9223372036854775807L, 9223372036854775807L, 9999999L, 0L });

    /// <summary>Checks the property for fixture case 53.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_53() => Check(new long[] { 0L, 9223372036854775807L, 9223372036854775807L, 9999999L, 2L });

    /// <summary>Checks the property for fixture case 54.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_54() => Check(new long[] { 0L, 6148914691236517205L, 6148914691236517205L, 15000000L, 0L });

    /// <summary>Checks the property for fixture case 55.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_55() => Check(new long[] { 0L, 6148914691236517205L, 6148914691236517205L, 15000000L, 2L });

    /// <summary>Checks the property for fixture case 56.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_56() => Check(new long[] { -1000000L, 250000L, 400000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 57.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_57() => Check(new long[] { -1000000L, 250000L, 400000L, 10000000L, 1L });

    /// <summary>Checks the property for fixture case 58.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_58() => Check(new long[] { -1000000L, 250000L, 400000L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 59.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_59() => Check(new long[] { -501L, 500L, 500L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 60.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_60() => Check(new long[] { -500L, 500L, 500L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 61.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_61() => Check(new long[] { -499L, 500L, 500L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 62.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_62() => Check(new long[] { -100L, 500L, 500L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 63.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_63() => Check(new long[] { 501L, 500L, 500L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 64.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_64() => Check(new long[] { 500L, 500L, 500L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 65.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_65() => Check(new long[] { 499L, 500L, 500L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 66.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_66() => Check(new long[] { 100L, 500L, 500L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 67.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_67() => Check(new long[] { -1L, 0L, 0L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 68.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_68() => Check(new long[] { -1L, 0L, 0L, 10000000L, 1L });

    /// <summary>Checks the property for fixture case 69.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_69() => Check(new long[] { -1L, 0L, 0L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 70.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_70() => Check(new long[] { -9223372036854775797L, 10L, 10L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 71.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_71() => Check(new long[] { -9223372036854775798L, 10L, 10L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 72.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_72() => Check(new long[] { -9223372036854775799L, 10L, 10L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 73.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_73() => Check(new long[] { -9223372036854775807L, 1L, 1L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 74.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_74() => Check(new long[] { long.MinValue, 1L, 1L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 75.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_75() => Check(new long[] { long.MinValue, 0L, 0L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 76.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_76() => Check(new long[] { 9223372036854775796L, 10L, 10L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 77.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_77() => Check(new long[] { 9223372036854775797L, 10L, 10L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 78.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_78() => Check(new long[] { 9223372036854775798L, 10L, 10L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 79.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_79() => Check(new long[] { 9223372036854775806L, 1L, 1L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 80.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_80() => Check(new long[] { 9223372036854775807L, 1L, 1L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 81.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_81() => Check(new long[] { 9223372036854775807L, 0L, 0L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 82.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_82() => Check(new long[] { long.MinValue, 10L, 10L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 83.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_83() => Check(new long[] { long.MinValue, 9223372036854775807L, 9223372036854775807L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 84.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_84() => Check(new long[] { 9223372036854775807L, 10L, 10L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 85.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_85() => Check(new long[] { 9223372036854775807L, 9223372036854775807L, 9223372036854775807L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 86.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_86() => Check(new long[] { long.MinValue, 9223372036854775807L, 9223372036854775807L, 10000000L, 1L });

    /// <summary>Checks the property for fixture case 87.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_87() => Check(new long[] { 9223372036854775807L, 9223372036854775807L, 9223372036854775807L, 10000000L, 1L });

    /// <summary>Checks the property for fixture case 88.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_88() => Check(new long[] { 1L, 9223372036854775807L, 9223372036854775807L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 89.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_89() => Check(new long[] { 0L, 9223372036854775807L, 9223372036854775807L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 90.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_90() => Check(new long[] { -1L, 9223372036854775807L, 9223372036854775807L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 91.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_91() => Check(new long[] { -2L, 9223372036854775807L, 9223372036854775807L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 92.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_92() => Check(new long[] { -1L, 9223372036854775807L, 9223372036854775807L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 93.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_93() => Check(new long[] { 0L, 9223372036854775807L, 9223372036854775807L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 94.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_94() => Check(new long[] { 1L, 9223372036854775807L, 9223372036854775807L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 95.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_95() => Check(new long[] { 2L, 9223372036854775807L, 9223372036854775807L, 10000000L, 2L });

    /// <summary>Checks the property for fixture case 96.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_96() => Check(new long[] { long.MinValue, 10000000L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 97.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_97() => Check(new long[] { -9223372036854775807L, 10000000L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 98.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_98() => Check(new long[] { 9223372036854775806L, 10000000L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 99.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_99() => Check(new long[] { 9223372036854775807L, 10000000L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 100.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_100() => Check(new long[] { 0L, 0L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 101.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_101() => Check(new long[] { 0L, 1L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 102.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_102() => Check(new long[] { 0L, 9223372036854775806L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 103.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_103() => Check(new long[] { 0L, 9223372036854775807L, 10000000L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 104.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_104() => Check(new long[] { 0L, 10000000L, 0L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 105.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_105() => Check(new long[] { 0L, 10000000L, 1L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 106.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_106() => Check(new long[] { 0L, 10000000L, 9223372036854775806L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 107.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_107() => Check(new long[] { 0L, 10000000L, 9223372036854775807L, 10000000L, 0L });

    /// <summary>Checks the property for fixture case 108.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_108() => Check(new long[] { 0L, 10000000L, 10000000L, 1L, 0L });

    /// <summary>Checks the property for fixture case 109.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_109() => Check(new long[] { 0L, 10000000L, 10000000L, 2L, 0L });

    /// <summary>Checks the property for fixture case 110.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_110() => Check(new long[] { 0L, 10000000L, 10000000L, 9223372036854775806L, 0L });

    /// <summary>Checks the property for fixture case 111.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_111() => Check(new long[] { 0L, 10000000L, 10000000L, 9223372036854775807L, 0L });

    /// <summary>Checks the property for fixture case 112.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_112() => Check(new long[] { 0L, 10000000L, 10000000L, 10000000L, 1L });

    /// <summary>Checks the property for fixture case 113.</summary>
    /// <returns>The assertion task.</returns>
    [Test]
    public Task Case_113() => Check(new long[] { 0L, 10000000L, 10000000L, 10000000L, 2L });

    private static async Task Check(long[] source)
    {
        if (!global::@Bifrost.@Tests.@Queues.@PriorityKeyBinding.@Precondition(source))
        {
            throw new InvalidOperationException("TestGen invalid case: source is outside the declared domain.");
        }

        long observed = global::@Bifrost.@Tests.@Queues.@PriorityKeyBinding.@Observe(source);
        await Assert.That(global::@Bifrost.@Tests.@Queues.@PriorityKeyBinding.@Predicate(source, observed)).IsTrue();
    }
}
